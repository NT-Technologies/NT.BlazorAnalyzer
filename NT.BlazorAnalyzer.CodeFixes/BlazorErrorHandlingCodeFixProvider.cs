using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace NT.BlazorAnalyzer;

[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(BlazorErrorHandlingCodeFixProvider)), Shared]
public sealed class BlazorErrorHandlingCodeFixProvider : CodeFixProvider
{
    private const string UseTaskTitle = "Change return type to Task";
    private const string AddTryCatchTitle = "Wrap body in try/catch";
    private const string AddInteractivityGuardTitle = "Guard with RendererInfo.IsInteractive";
    private const string RethrowExceptionTitle = "Rethrow exception";
    private const string AddErrorContentTitle = "Add ErrorContent";

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

    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

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
            if (methodDeclaration is null || !CanWrapMethod(methodDeclaration))
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    AddTryCatchTitle,
                    cancellationToken => WrapMethodInTryCatchAsync(context.Document, methodDeclaration, cancellationToken),
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
            if (catchClause is null || catchClause.Block.Statements.OfType<ThrowStatementSyntax>().Any())
            {
                return;
            }

            context.RegisterCodeFix(
                CodeAction.Create(
                    RethrowExceptionTitle,
                    cancellationToken => AddRethrowAsync(context.Document, catchClause, cancellationToken),
                    equivalenceKey: RethrowExceptionTitle),
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
        }
    }

    private static bool CanWrapMethod(MethodDeclarationSyntax methodDeclaration) =>
        methodDeclaration.Body is not null || methodDeclaration.ExpressionBody is not null;

    private static bool CanGuardMethod(MethodDeclarationSyntax methodDeclaration)
    {
        if (methodDeclaration.Body is null && methodDeclaration.ExpressionBody is null)
        {
            return false;
        }

        return !methodDeclaration.DescendantNodes().OfType<IdentifierNameSyntax>()
            .Any(static identifier => string.Equals(identifier.Identifier.ValueText, "RendererInfo", StringComparison.Ordinal));
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

    private static async Task<Document> WrapMethodInTryCatchAsync(
        Document document,
        MethodDeclarationSyntax methodDeclaration,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var updatedMethod = methodDeclaration.Body is not null
            ? methodDeclaration.WithBody(CreateTryCatchBody(methodDeclaration.Body.Statements))
            : methodDeclaration
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithBody(CreateTryCatchBody(
                    [
                        SyntaxFactory.ExpressionStatement(methodDeclaration.ExpressionBody!.Expression)
                    ]));

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

    private static async Task<Document> AddRethrowAsync(
        Document document,
        CatchClauseSyntax catchClause,
        CancellationToken cancellationToken)
    {
        var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return document;
        }

        var throwStatement = SyntaxFactory.ThrowStatement();
        var updatedCatch = catchClause.WithBlock(catchClause.Block.AddStatements(throwStatement));
        return document.WithSyntaxRoot(root.ReplaceNode(catchClause, updatedCatch));
    }

    private static BlockSyntax CreateTryCatchBody(SyntaxList<StatementSyntax> statements) =>
        SyntaxFactory.Block(
            SyntaxFactory.TryStatement(
                SyntaxFactory.Block(statements),
                SyntaxFactory.SingletonList(
                    SyntaxFactory.CatchClause()
                        .WithDeclaration(
                            SyntaxFactory.CatchDeclaration(
                                SyntaxFactory.ParseTypeName("global::System.Exception"),
                                SyntaxFactory.Identifier("ex")))
                        .WithBlock(
                            SyntaxFactory.Block(
                                SyntaxFactory.SingletonList<StatementSyntax>(SyntaxFactory.ThrowStatement())))),
                default));

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
                childIndentation + "<ErrorContent Context=\"exception\">",
                childIndentation + "    <p>@exception.Message</p>",
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
