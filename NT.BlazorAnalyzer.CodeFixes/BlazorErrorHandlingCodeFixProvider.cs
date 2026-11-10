using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;

namespace NT.BlazorAnalyzer;

/// <summary>
/// Provides fixes for supported Blazor error handling diagnostics.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(BlazorErrorHandlingCodeFixProvider)), Shared]
public sealed class BlazorErrorHandlingCodeFixProvider : CodeFixProvider
{
    private const string UseTaskTitle = "Change return type to Task";
    private const string AddTryCatchTitle = "Wrap body in try/catch and log";
    private const string AddInteractivityGuardTitle = "Guard with RendererInfo.IsInteractive";
    private const string LogExceptionTitle = "Log exception";
    private const string AddErrorContentTitle = "Add ErrorContent";

    /// <inheritdoc />
    public override ImmutableArray<string> FixableDiagnosticIds =>
    [
        "NTBA0003",
        "NTBA0004",
        "NTBA0005",
        "NTBA0006",
        "NTBA0007",
        "NTBA0008",
        "NTBA0009"
    ];

    /// <inheritdoc />
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <inheritdoc />
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        var diagnostic = context.Diagnostics.First();
        var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        if (diagnostic.Id is "NTBA0003" or "NTBA0004" or "NTBA0005")
        {
            var methodDeclaration = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (methodDeclaration is null || !CanWrapMethod(methodDeclaration) ||
                await FindLoggerAsync(context.Document, methodDeclaration, context.CancellationToken).ConfigureAwait(false) is not { } logger)
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    AddTryCatchTitle,
                    cancellationToken => ApplyWithLoggerAsync(context.Document, methodDeclaration, WrapMethodInTryCatch(methodDeclaration, logger.Name), logger, cancellationToken),
                    equivalenceKey: AddTryCatchTitle),
                diagnostic);
            return;
        }

        if (diagnostic.Id == "NTBA0006")
        {
            var methodDeclaration = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (methodDeclaration is null || !CanGuardMethod(methodDeclaration))
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    AddInteractivityGuardTitle,
                    cancellationToken => GuardMethodWithInteractivityAsync(context.Document, methodDeclaration, cancellationToken),
                    equivalenceKey: AddInteractivityGuardTitle),
                diagnostic);
            return;
        }

        if (diagnostic.Id == "NTBA0007")
        {
            var methodDeclaration = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<MethodDeclarationSyntax>();
            if (methodDeclaration is null ||
                !methodDeclaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword)) ||
                methodDeclaration.ReturnType is not PredefinedTypeSyntax predefinedType ||
                !predefinedType.Keyword.IsKind(SyntaxKind.VoidKeyword))
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    UseTaskTitle,
                    cancellationToken => ChangeReturnTypeToTaskAsync(context.Document, methodDeclaration, cancellationToken),
                    equivalenceKey: UseTaskTitle),
                diagnostic);
            return;
        }

        if (diagnostic.Id == "NTBA0008")
        {
            var catchClause = root.FindNode(context.Span, getInnermostNodeForTie: true).FirstAncestorOrSelf<CatchClauseSyntax>();
            if (catchClause is null || catchClause.Block.Statements.OfType<ThrowStatementSyntax>().Any() ||
                await FindLoggerAsync(context.Document, catchClause, context.CancellationToken).ConfigureAwait(false) is not { } logger)
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    LogExceptionTitle,
                    cancellationToken => ApplyWithLoggerAsync(context.Document, catchClause, AddLogging(catchClause, logger.Name), logger, cancellationToken),
                    equivalenceKey: LogExceptionTitle),
                diagnostic);
            return;
        }

        if (diagnostic.Id == "NTBA0009")
        {
            var sourceText = await context.Document.GetTextAsync(context.CancellationToken).ConfigureAwait(false);
            if (!TryFindBoundaryInsertion(sourceText, context.Span.Start, out var insertionPosition, out var indentation))
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    AddErrorContentTitle,
                    cancellationToken => AddErrorContentAsync(context.Document, sourceText, insertionPosition, indentation, cancellationToken),
                    equivalenceKey: AddErrorContentTitle),
                diagnostic);
            return;
        }

    }

    // A catch can only contain failures of void or async Task/ValueTask methods; a non-async Task method fails after it returns.
    private static bool CanWrapMethod(MethodDeclarationSyntax methodDeclaration) =>
        (methodDeclaration.Body is not null || methodDeclaration.ExpressionBody is not null) && ReturnsNoValue(methodDeclaration);

    /// <summary>
    /// Finds an ILogger field or property on the component, or proposes an injected <c>Logger</c> property when none exists.
    /// </summary>
    private static async Task<(string Name, bool Inject)?> FindLoggerAsync(Document document, SyntaxNode node, CancellationToken cancellationToken)
    {
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (semanticModel is null ||
            semanticModel.Compilation.GetTypeByMetadataName("Microsoft.Extensions.Logging.ILogger") is not { } loggerType ||
            node.FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } typeDeclaration ||
            semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is not { } componentType)
        {
            return null;
        }

        for (var current = componentType; current is not null; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                var memberType = member switch
                {
                    IFieldSymbol field => field.Type,
                    IPropertySymbol property => property.Type,
                    _ => null
                };

                if (memberType is not null &&
                    !member.IsImplicitlyDeclared &&
                    (SymbolEqualityComparer.Default.Equals(current, componentType) || member.DeclaredAccessibility != Accessibility.Private) &&
                    (SymbolEqualityComparer.Default.Equals(memberType, loggerType) || memberType.AllInterfaces.Contains(loggerType, SymbolEqualityComparer.Default)))
                {
                    return (member.Name, false);
                }
            }
        }

        // Only a member named Logger blocks injection; an imported type such as Microsoft.Extensions.Logging.Logger<T> does not.
        return semanticModel.LookupSymbols(node.SpanStart, container: componentType, name: "Logger").IsEmpty ? ("Logger", true) : null;
    }

    private static async Task<Document> ApplyWithLoggerAsync(Document document, SyntaxNode target, SyntaxNode replacement, (string Name, bool Inject) logger, CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || semanticModel is null || target.FirstAncestorOrSelf<TypeDeclarationSyntax>() is not { } typeDeclaration)
        {
            return document;
        }

        var updatedType = typeDeclaration.ReplaceNode(target, replacement.WithAdditionalAnnotations(Formatter.Annotation));
        if (logger.Inject && semanticModel.GetDeclaredSymbol(typeDeclaration, cancellationToken) is { } componentType)
        {
            var loggerProperty = SyntaxFactory.ParseMemberDeclaration(
                $"[global::Microsoft.AspNetCore.Components.Inject] private global::Microsoft.Extensions.Logging.ILogger<{componentType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}> {logger.Name} {{ get; set; }} = default!;")!;
            updatedType = updatedType.WithMembers(updatedType.Members.Insert(0, loggerProperty.WithAdditionalAnnotations(Formatter.Annotation, Simplifier.Annotation)));
        }

        var updatedRoot = root.ReplaceNode(typeDeclaration, updatedType);
        if (updatedRoot is CompilationUnitSyntax compilationUnit &&
            semanticModel.LookupNamespacesAndTypes(target.SpanStart, name: "LoggerExtensions").IsEmpty)
        {
            updatedRoot = compilationUnit.AddUsings(
                SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Microsoft.Extensions.Logging")).WithAdditionalAnnotations(Formatter.Annotation));
        }

        return document.WithSyntaxRoot(updatedRoot);
    }

    private static MethodDeclarationSyntax WrapMethodInTryCatch(MethodDeclarationSyntax methodDeclaration, string loggerName)
    {
        var statements = methodDeclaration.Body?.Statements ??
            SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ExpressionStatement(methodDeclaration.ExpressionBody!.Expression));
        var exceptionName = GetUnusedExceptionName(methodDeclaration);
        var tryStatement = SyntaxFactory.TryStatement(
            SyntaxFactory.Block(statements),
            SyntaxFactory.SingletonList(
                SyntaxFactory.CatchClause()
                    .WithDeclaration(SyntaxFactory.CatchDeclaration(SyntaxFactory.ParseTypeName("global::System.Exception"), SyntaxFactory.Identifier(exceptionName)))
                    .WithBlock(SyntaxFactory.Block(CreateLogStatement(loggerName, exceptionName, methodDeclaration.Identifier.ValueText)))),
            null);

        return methodDeclaration
            .WithExpressionBody(null)
            .WithSemicolonToken(default)
            .WithBody(SyntaxFactory.Block(tryStatement));
    }

    private static CatchClauseSyntax AddLogging(CatchClauseSyntax catchClause, string loggerName)
    {
        var exceptionName = catchClause.Declaration?.Identifier.ValueText is { Length: > 0 } declaredName
            ? declaredName
            : GetUnusedExceptionName(catchClause.FirstAncestorOrSelf<MemberDeclarationSyntax>() ?? (SyntaxNode)catchClause);
        var declaration = SyntaxFactory.CatchDeclaration(
            catchClause.Declaration?.Type ?? SyntaxFactory.ParseTypeName("global::System.Exception"),
            SyntaxFactory.Identifier(exceptionName));
        var methodName = catchClause.FirstAncestorOrSelf<MethodDeclarationSyntax>()?.Identifier.ValueText ?? "Operation";

        return catchClause
            .WithCatchKeyword(catchClause.CatchKeyword.WithTrailingTrivia(SyntaxFactory.Space))
            .WithDeclaration(declaration)
            .WithBlock(catchClause.Block.WithStatements(catchClause.Block.Statements.Insert(0, CreateLogStatement(loggerName, exceptionName, methodName))));
    }

    private static StatementSyntax CreateLogStatement(string loggerName, string exceptionName, string methodName) =>
        SyntaxFactory.ParseStatement($"{loggerName}.LogError({exceptionName}, \"{methodName} failed.\");");

    private static string GetUnusedExceptionName(SyntaxNode scope)
    {
        var usedNames = scope.DescendantTokens()
            .Where(static token => token.IsKind(SyntaxKind.IdentifierToken))
            .Select(static token => token.ValueText)
            .ToImmutableHashSet(StringComparer.Ordinal);
        return new[] { "exception", "caughtException" }
            .Concat(Enumerable.Range(2, int.MaxValue - 2).Select(static index => "caughtException" + index))
            .First(name => !usedNames.Contains(name));
    }

    private static bool CanGuardMethod(MethodDeclarationSyntax methodDeclaration)
    {
        if ((methodDeclaration.Body is null && methodDeclaration.ExpressionBody is null) || !ReturnsNoValue(methodDeclaration))
        {
            return false;
        }

        return !methodDeclaration.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(static identifier => string.Equals(identifier.Identifier.ValueText, "RendererInfo", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns true when the method's return type is void, or the method is async and returns non-generic Task or ValueTask.
    /// </summary>
    private static bool ReturnsNoValue(MethodDeclarationSyntax methodDeclaration)
    {
        if (methodDeclaration.ReturnType is PredefinedTypeSyntax predefinedType)
        {
            return predefinedType.Keyword.IsKind(SyntaxKind.VoidKeyword);
        }

        var isAsync = methodDeclaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword));
        var returnTypeName = methodDeclaration.ReturnType switch
        {
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            QualifiedNameSyntax { Right: IdentifierNameSyntax identifier } => identifier.Identifier.ValueText,
            _ => null
        };

        return isAsync && returnTypeName is "Task" or "ValueTask";
    }

    private static async Task<Document> ChangeReturnTypeToTaskAsync(
        Document document,
        MethodDeclarationSyntax methodDeclaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var taskType = SyntaxFactory.ParseTypeName("global::System.Threading.Tasks.Task")
            .WithTriviaFrom(methodDeclaration.ReturnType);
        var updatedMethod = methodDeclaration.WithReturnType(taskType);
        return document.WithSyntaxRoot(root.ReplaceNode(methodDeclaration, updatedMethod));
    }

    private static async Task<Document> GuardMethodWithInteractivityAsync(
        Document document,
        MethodDeclarationSyntax methodDeclaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var executableStatements = methodDeclaration.Body is not null
            ? methodDeclaration.Body.Statements
            : SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ExpressionStatement(methodDeclaration.ExpressionBody!.Expression));
        var guardedBlock = SyntaxFactory.Block(
            SyntaxFactory.IfStatement(
                SyntaxFactory.ParseExpression("RendererInfo.IsInteractive"),
                SyntaxFactory.Block(executableStatements)));

        var updatedMethod = methodDeclaration.Body is not null
            ? methodDeclaration.WithBody(guardedBlock)
            : methodDeclaration
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithBody(guardedBlock);

        return document.WithSyntaxRoot(root.ReplaceNode(methodDeclaration, updatedMethod));
    }

    private static Task<Document> AddErrorContentAsync(
        Document document,
        SourceText sourceText,
        int insertionPosition,
        string indentation,
        CancellationToken cancellationToken)
    {
        var newline = DetectNewLine(sourceText);
        var childIndentation = indentation + "    ";
        var block = string.Join(
            newline,
            new[]
            {
                string.Empty,
                childIndentation + "<ErrorContent>",
                childIndentation + "    <p>Something went wrong. Please try again.</p>",
                childIndentation + "</ErrorContent>"
            });

        var updatedText = sourceText.WithChanges(new TextChange(new TextSpan(insertionPosition, 0), block));
        return Task.FromResult(document.WithText(updatedText));
    }

    private static bool TryFindBoundaryInsertion(SourceText sourceText, int diagnosticPosition, out int insertionPosition, out string indentation)
    {
        var text = sourceText.ToString();
        insertionPosition = -1;
        indentation = string.Empty;

        if (!TryFindOpeningTag(text, diagnosticPosition, out var openingTagStart, out var tagName, out var tagEnd, out var selfClosing) ||
            selfClosing)
        {
            return false;
        }

        var closingTagStart = FindMatchingClosingTag(text, tagName, openingTagStart, tagEnd);
        if (closingTagStart < 0)
        {
            return false;
        }

        var line = sourceText.Lines.GetLineFromPosition(openingTagStart);
        indentation = GetLeadingWhitespace(text, line.Start, openingTagStart);
        insertionPosition = closingTagStart;
        return true;
    }

    private static bool TryFindOpeningTag(string text, int position, out int tagStart, out string tagName, out int tagEnd, out bool selfClosing)
    {
        tagStart = -1;
        tagName = string.Empty;
        tagEnd = -1;
        selfClosing = false;

        for (var index = Math.Min(position, text.Length - 1); index >= 0; index--)
        {
            if (text[index] != '<')
            {
                continue;
            }

            if (!TryParseTag(text, index, out var parsedTag) || parsedTag.IsClosingTag)
            {
                continue;
            }

            tagStart = index;
            tagName = parsedTag.Name;
            tagEnd = parsedTag.EndIndex;
            selfClosing = parsedTag.IsSelfClosing;
            return true;
        }

        return false;
    }

    private static int FindMatchingClosingTag(string text, string tagName, int searchStart, int contentStart)
    {
        var depth = 0;
        for (var index = searchStart; index < text.Length; index++)
        {
            if (text[index] != '<' || !TryParseTag(text, index, out var parsedTag) || !string.Equals(parsedTag.Name, tagName, StringComparison.Ordinal))
            {
                continue;
            }

            if (!parsedTag.IsClosingTag)
            {
                if (!parsedTag.IsSelfClosing)
                {
                    depth++;
                }
            }
            else
            {
                depth--;
                if (depth == 0 && index >= contentStart)
                {
                    return index;
                }
            }

            index = parsedTag.EndIndex - 1;
        }

        return -1;
    }

    private static bool TryParseTag(string text, int startIndex, out ParsedTag tag)
    {
        tag = default;
        if (startIndex < 0 || startIndex >= text.Length || text[startIndex] != '<')
        {
            return false;
        }

        var index = startIndex + 1;
        var isClosingTag = false;
        if (index < text.Length && text[index] == '/')
        {
            isClosingTag = true;
            index++;
        }

        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        var nameStart = index;
        while (index < text.Length && IsTagNameCharacter(text[index]))
        {
            index++;
        }

        if (nameStart == index)
        {
            return false;
        }

        var name = text.Substring(nameStart, index - nameStart);
        var inQuote = false;
        var quote = '\0';
        var selfClosing = false;

        while (index < text.Length)
        {
            var current = text[index];
            if (inQuote)
            {
                if (current == quote)
                {
                    inQuote = false;
                }

                index++;
                continue;
            }

            if (current is '"' or '\'')
            {
                inQuote = true;
                quote = current;
                index++;
                continue;
            }

            if (current == '>')
            {
                selfClosing = index > startIndex && text[index - 1] == '/';
                tag = new ParsedTag(name, isClosingTag, selfClosing, index + 1);
                return true;
            }

            index++;
        }

        return false;
    }

    private static string DetectNewLine(SourceText sourceText)
    {
        if (sourceText.Lines.Count <= 1)
        {
            return "\n";
        }

        var start = sourceText.Lines[0].End;
        var length = sourceText.Lines[1].Start - start;
        return sourceText.ToString(new TextSpan(start, length));
    }

    private static string GetLeadingWhitespace(string text, int lineStart, int position)
    {
        var length = 0;
        while (lineStart + length < position && (text[lineStart + length] == ' ' || text[lineStart + length] == '\t'))
        {
            length++;
        }

        return text.Substring(lineStart, length);
    }

    private static bool IsTagNameCharacter(char value) =>
        char.IsLetterOrDigit(value) ||
        value is '_' or ':' or '.' or '-';

    private readonly struct ParsedTag
    {
        public ParsedTag(string name, bool isClosingTag, bool isSelfClosing, int endIndex)
        {
            Name = name;
            IsClosingTag = isClosingTag;
            IsSelfClosing = isSelfClosing;
            EndIndex = endIndex;
        }

        public string Name { get; }

        public bool IsClosingTag { get; }

        public bool IsSelfClosing { get; }

        public int EndIndex { get; }
    }
}
