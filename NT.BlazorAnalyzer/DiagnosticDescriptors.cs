using Microsoft.CodeAnalysis;

namespace NT.BlazorAnalyzer;

internal static class DiagnosticDescriptors
{
    private const string Category = "Reliability";

    public static readonly DiagnosticDescriptor MissingErrorBoundary = new(
        id: "NTBA0001",
        title: "Interactive render region should be protected by ErrorBoundary",
        messageFormat: "Interactive render region in component '{0}' should be protected by ErrorBoundary. Wrapping '{1}' in an ErrorBoundary resolves this warning. Suggested scope: '{2}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Each independently interactive, user-visible render region in a Blazor component hierarchy should be covered by ErrorBoundary or a derived component at an appropriate containment level.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public static readonly DiagnosticDescriptor MissingTryCatch = new(
        id: "NTBA0002",
        title: "Uncovered interactive entry method should use try/catch",
        messageFormat: "Method '{0}' in interactive component '{1}' performs failure-prone work reachable from an uncovered interactive region without try/catch handling",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Failure-prone interactive entry methods reachable from independently interactive render regions without ErrorBoundary coverage should use try/catch handling or delegate entirely to a safe member method.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public static readonly DiagnosticDescriptor LifecycleMissingTryCatch = new(
        id: "NTBA0003",
        title: "Lifecycle method should use meaningful exception handling or owner ErrorBoundary coverage",
        messageFormat: "{2} lifecycle method '{0}' in interactive component '{1}' performs failure-prone work without meaningful exception handling or owner ErrorBoundary coverage",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Failure-prone interactive Blazor lifecycle methods should use meaningful exception handling or be rendered only through known owner components that are covered by ErrorBoundary. Early lifecycle failures are especially risky during prerendering and circuit initialization, and delegation to a safe handler is acceptable.");

    public static readonly DiagnosticDescriptor DisposeMissingTryCatch = new(
        id: "NTBA0004",
        title: "Dispose method should use meaningful exception handling",
        messageFormat: "Dispose method '{0}' in interactive component '{1}' performs failure-prone cleanup without meaningful exception handling",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Failure-prone cleanup in interactive Blazor Dispose and DisposeAsync methods should use meaningful exception handling. Delegation to a safe local cleanup helper is acceptable.");

    public static readonly DiagnosticDescriptor JsInteropMissingTryCatch = new(
        id: "NTBA0005",
        title: "JS interop should use meaningful exception handling",
        messageFormat: "Method '{0}' in interactive component '{1}' performs JS interop without meaningful exception handling",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "JS interop calls in interactive Blazor components should use meaningful exception handling. Logged or rethrown catches are accepted, and DisposeAsync cleanup may intentionally catch JSDisconnectedException.");

    public static readonly DiagnosticDescriptor JsInteropRequiresInteractivityGuard = new(
        id: "NTBA0006",
        title: "Early lifecycle JS interop should use a recognized interactivity check",
        messageFormat: "Lifecycle method '{0}' in interactive component '{1}' performs JS interop before OnAfterRender without a recognized interactivity check",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "JS interop in initialization and parameter lifecycle methods should be guarded by a recognized interactivity check such as RendererInfo.IsInteractive or AssignedRenderMode being non-null, or moved to OnAfterRender.");

    public static readonly DiagnosticDescriptor AsyncVoidMethod = new(
        id: "NTBA0007",
        title: "Async component method should return Task",
        messageFormat: "Async method '{0}' in interactive component '{1}' returns void; return Task or ValueTask so Blazor can observe exceptions",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Interactive Blazor component async methods should return Task or ValueTask instead of async void so the framework can observe completion and exception flow.");

    public static readonly DiagnosticDescriptor CatchWithoutLogging = new(
        id: "NTBA0008",
        title: "Caught exceptions should report the caught exception or rethrow",
        messageFormat: "Catch block in method '{0}' in interactive component '{1}' should log or report the caught exception object, or rethrow it",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Exception handling in interactive Blazor components should preserve the caught exception through logging, telemetry, or rethrowing.");

    public static readonly DiagnosticDescriptor ErrorBoundaryMissingErrorContent = new(
        id: "NTBA0009",
        title: "Root ErrorBoundary should consider custom ErrorContent",
        messageFormat: "Interactive component '{0}' opens ErrorBoundary first but relies on the default ErrorBoundary fallback UI",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Custom ErrorContent is optional in Blazor, but user-facing root ErrorBoundary components usually benefit from a clearer fallback experience than the default UI.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public static readonly DiagnosticDescriptor LayoutBoundaryShouldBeRouteKeyed = new(
        id: "NTBA0010",
        title: "Static layout ErrorBoundary has limited interactive coverage",
        messageFormat: "Layout component '{0}' uses ErrorBoundary in a static layout. In Blazor Web Apps this only covers static SSR and won't catch interactive event failures unless app routes are globally interactive.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A layout-level ErrorBoundary in a static layout only covers static SSR in Blazor Web Apps. Prefer narrower page or widget boundaries, or adopt globally interactive app routes when broad layout recovery is intentional.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);
}
