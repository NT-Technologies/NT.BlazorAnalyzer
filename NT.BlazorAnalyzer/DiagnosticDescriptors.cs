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
        title: "Lifecycle method should use meaningful exception handling",
        messageFormat: "{2} lifecycle method '{0}' in interactive component '{1}' performs failure-prone work without meaningful exception handling",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Failure-prone interactive Blazor lifecycle methods should use meaningful exception handling. Early lifecycle failures are especially risky during prerendering and circuit initialization, and delegation to a safe handler is acceptable.");

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
        title: "JS interop should be guarded by interactivity in early lifecycle methods",
        messageFormat: "Lifecycle method '{0}' in interactive component '{1}' performs JS interop before OnAfterRender without an interactivity guard",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "JS interop in initialization and parameter lifecycle methods should be guarded by an interactivity check or moved to OnAfterRender.");

    public static readonly DiagnosticDescriptor AsyncVoidMethod = new(
        id: "NTBA0007",
        title: "Component method should not be async void",
        messageFormat: "Method '{0}' in interactive component '{1}' is async void",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Interactive Blazor component methods should return Task instead of async void so exceptions can be observed and handled.");

    public static readonly DiagnosticDescriptor CatchWithoutLogging = new(
        id: "NTBA0008",
        title: "Caught exceptions should be logged or rethrown",
        messageFormat: "Catch block in method '{0}' in interactive component '{1}' should log, track, or rethrow the exception",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Exception handling in interactive Blazor components should preserve diagnostics through logging, telemetry, or rethrowing.");

    public static readonly DiagnosticDescriptor ErrorBoundaryMissingErrorContent = new(
        id: "NTBA0009",
        title: "Root ErrorBoundary should define ErrorContent",
        messageFormat: "Interactive component '{0}' opens ErrorBoundary first but does not provide ErrorContent",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Root ErrorBoundary components should provide ErrorContent so failures are surfaced meaningfully to users.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public static readonly DiagnosticDescriptor LayoutBoundaryShouldBeRouteKeyed = new(
        id: "NTBA0010",
        title: "Layouts should avoid ErrorBoundary",
        messageFormat: "Layout component '{0}' uses ErrorBoundary. Prefer page/widget boundaries instead of wrapping layouts.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Layout-level ErrorBoundary usage can reset shared layout state and recreate long-lived UI hosts across navigation. Prefer placing boundaries at page or widget scope.");
}
