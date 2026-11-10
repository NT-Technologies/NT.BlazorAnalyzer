using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NT.BlazorAnalyzer;

/// <summary>
/// Reports component work whose failures run outside Blazor's error handling: fire-and-forget tasks (NTBA0011)
/// and callbacks raised by timers, navigation, or application services (NTBA0012).
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BackgroundFailureAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [DiagnosticDescriptors.UnobservedComponentTask, DiagnosticDescriptors.UnhandledExternalCallback];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.RegisterCompilationStartAction(static startContext =>
        {
            var componentBase = startContext.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.ComponentBase");
            var renderModeAttribute = startContext.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.RenderModeAttribute");
            if (componentBase is null || renderModeAttribute is null)
            {
                return;
            }

            var settings = AnalyzerSettings.Create(startContext.Options.AnalyzerConfigOptionsProvider, startContext.Compilation);
            startContext.RegisterSyntaxNodeAction(
                nodeContext =>
                {
                    if (GetInteractiveComponent(nodeContext, componentBase, renderModeAttribute, settings) is not { } component)
                    {
                        return;
                    }

                    if (nodeContext.Node is AssignmentExpressionSyntax assignment && assignment.IsKind(SyntaxKind.AddAssignmentExpression))
                    {
                        AnalyzeSubscription(nodeContext, assignment, component, settings);
                    }
                    else if (nodeContext.Node is BaseObjectCreationExpressionSyntax creation)
                    {
                        AnalyzeTimerCreation(nodeContext, creation, component, settings);
                    }
                    else
                    {
                        AnalyzeUnobservedTask(nodeContext, component, settings);
                    }
                },
                SyntaxKind.AddAssignmentExpression,
                SyntaxKind.SimpleAssignmentExpression,
                SyntaxKind.ExpressionStatement,
                SyntaxKind.ObjectCreationExpression,
                SyntaxKind.ImplicitObjectCreationExpression);
        });
    }

    private static INamedTypeSymbol? GetInteractiveComponent(SyntaxNodeAnalysisContext context, INamedTypeSymbol componentBase, INamedTypeSymbol renderModeAttribute, AnalyzerSettings settings) =>
        context.ContainingSymbol?.ContainingType is { } type &&
        BlazorErrorHandlingAnalyzer.IsComponent(type, componentBase) &&
        (BlazorErrorHandlingAnalyzer.GetDeclaredRenderModeKey(type, renderModeAttribute) ?? settings.DefaultRenderMode) is not null
            ? type
            : null;

    private static void AnalyzeUnobservedTask(SyntaxNodeAnalysisContext context, INamedTypeSymbol component, AnalyzerSettings settings)
    {
        // `_ = WorkAsync();` anywhere, or `WorkAsync();` in a non-async body where CS4014 does not apply.
        var taskExpression = context.Node switch
        {
            AssignmentExpressionSyntax { Left: IdentifierNameSyntax { Identifier.ValueText: "_" } } discard
                when context.SemanticModel.GetSymbolInfo(discard.Left, context.CancellationToken).Symbol is IDiscardSymbol => discard.Right,
            ExpressionStatementSyntax { Expression: InvocationExpressionSyntax statementInvocation } when !IsInAsyncFunction(statementInvocation) => statementInvocation,
            _ => null
        };

        if (taskExpression is InvocationExpressionSyntax taskInvocation &&
            StripConfigureAwait(taskInvocation) is var invocation &&
            IsAwaitable(context.SemanticModel.GetTypeInfo(invocation, context.CancellationToken).Type) &&
            IsRiskyTask(context, invocation, component, settings))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnobservedComponentTask,
                invocation.GetLocation(),
                GetDisplayName(invocation),
                component.Name));
        }
    }

    private static void AnalyzeSubscription(SyntaxNodeAnalysisContext context, AssignmentExpressionSyntax assignment, INamedTypeSymbol component, AnalyzerSettings settings)
    {
        if (context.SemanticModel.GetSymbolInfo(assignment.Left, context.CancellationToken).Symbol is IEventSymbol eventSymbol && IsExternalEvent(eventSymbol))
        {
            ReportUnhandledCallback(context, assignment.Right, assignment, component, settings);
        }
    }

    private static void AnalyzeTimerCreation(SyntaxNodeAnalysisContext context, BaseObjectCreationExpressionSyntax creation, INamedTypeSymbol component, AnalyzerSettings settings)
    {
        if (creation.ArgumentList?.Arguments.FirstOrDefault()?.Expression is { } callback &&
            context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type?.ToDisplayString() == "System.Threading.Timer")
        {
            ReportUnhandledCallback(context, callback, creation, component, settings);
        }
    }

    private static void ReportUnhandledCallback(SyntaxNodeAnalysisContext context, ExpressionSyntax callback, SyntaxNode reportNode, INamedTypeSymbol component, AnalyzerSettings settings)
    {
        // `new ElapsedEventHandler(OnElapsed)` wraps the real callback in an explicit delegate creation.
        if (callback is ObjectCreationExpressionSyntax { ArgumentList.Arguments.Count: 1 } delegateCreation &&
            context.SemanticModel.GetTypeInfo(delegateCreation, context.CancellationToken).Type?.TypeKind == TypeKind.Delegate)
        {
            callback = delegateCreation.ArgumentList.Arguments[0].Expression;
        }

        var (callbackBody, callbackModel) = callback switch
        {
            AnonymousFunctionExpressionSyntax lambda => (lambda.Body, context.SemanticModel),
            _ when context.SemanticModel.GetSymbolInfo(callback, context.CancellationToken).Symbol is IMethodSymbol method &&
                   SymbolEqualityComparer.Default.Equals(method.ContainingType, component) => GetMemberBody(method, context, settings),
            _ => (null, context.SemanticModel)
        };

        // Unknown or external targets are not analyzed, so they are never reported.
        if (BlazorErrorHandlingAnalyzer.HasUnhandledFailureProneOperationIn(callbackBody, callbackModel, component, treatExplicitThrowsAsFailureProne: true, settings, context.CancellationToken))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnhandledExternalCallback,
                reportNode.GetLocation(),
                callback is AnonymousFunctionExpressionSyntax ? "lambda" : callback.ToString(),
                component.Name));
        }
    }

    private static bool IsRiskyTask(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, INamedTypeSymbol component, AnalyzerSettings settings)
    {
        // A delegate argument, as in Task.Run(...) or InvokeAsync(...), runs inside the unobserved task.
        if (invocation.ArgumentList.Arguments.Any(argument => argument.Expression is AnonymousFunctionExpressionSyntax lambda &&
                BlazorErrorHandlingAnalyzer.HasUnhandledFailureProneOperationIn(lambda.Body, context.SemanticModel, component, treatExplicitThrowsAsFailureProne: true, settings, context.CancellationToken)))
        {
            return true;
        }

        if (BlazorErrorHandlingAnalyzer.GetInvokedMemberMethod(invocation, context.SemanticModel, component, context.CancellationToken) is { } memberMethod)
        {
            var (body, model) = GetMemberBody(memberMethod, context, settings);
            return BlazorErrorHandlingAnalyzer.HasUnhandledFailureProneOperationIn(body, model, component, treatExplicitThrowsAsFailureProne: true, settings, context.CancellationToken);
        }

        return BlazorErrorHandlingAnalyzer.IsFailureProneInvocation(invocation, context.SemanticModel, component, settings, context.CancellationToken, isAwaited: true);
    }

    // A component member may live in another partial file (the .razor @code block or its code-behind), so bind it in its own tree.
    private static (SyntaxNode? Body, SemanticModel Model) GetMemberBody(IMethodSymbol method, SyntaxNodeAnalysisContext context, AnalyzerSettings settings)
    {
        var body = settings.TryGetSourceBody(method, context.CancellationToken);
        return (body, body is null || body.SyntaxTree == context.SemanticModel.SyntaxTree ? context.SemanticModel : settings.GetSemanticModel(body.SyntaxTree));
    }

    // Timers, navigation and application service events call back outside the renderer's call stack.
    // Framework events such as EditContext.OnFieldChanged are raised inside component event handlers and stay excluded.
    private static bool IsExternalEvent(IEventSymbol eventSymbol)
    {
        var containingType = eventSymbol.ContainingType.ToDisplayString();
        if (containingType is "System.Timers.Timer" or "Microsoft.AspNetCore.Components.NavigationManager")
        {
            return true;
        }

        var containingNamespace = eventSymbol.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        return !IsInNamespace(containingNamespace, "System") && !IsInNamespace(containingNamespace, "Microsoft");

        // Match whole namespace segments, so an app namespace such as Systematic.Events is not treated as the framework.
        static bool IsInNamespace(string candidate, string root) =>
            candidate == root || candidate.StartsWith(root + ".", StringComparison.Ordinal);
    }

    private static bool IsInAsyncFunction(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case AnonymousFunctionExpressionSyntax lambda:
                    return lambda.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword);
                case LocalFunctionStatementSyntax localFunction:
                    return localFunction.Modifiers.Any(SyntaxKind.AsyncKeyword);
                case MethodDeclarationSyntax method:
                    return method.Modifiers.Any(SyntaxKind.AsyncKeyword);
            }
        }

        return false;
    }

    private static bool IsAwaitable(ITypeSymbol? type) =>
        type?.OriginalDefinition.ToDisplayString() is
            "System.Threading.Tasks.Task" or "System.Threading.Tasks.Task<TResult>" or
            "System.Threading.Tasks.ValueTask" or "System.Threading.Tasks.ValueTask<TResult>";

    private static InvocationExpressionSyntax StripConfigureAwait(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ConfigureAwait", Expression: InvocationExpressionSyntax inner }
            ? inner
            : invocation;

    private static string GetDisplayName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
        SimpleNameSyntax simpleName => simpleName.Identifier.ValueText,
        _ => invocation.Expression.ToString()
    };
}
