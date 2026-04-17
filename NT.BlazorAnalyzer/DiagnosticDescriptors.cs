using Microsoft.CodeAnalysis;

namespace NT.BlazorAnalyzer;

internal static class DiagnosticDescriptors
{
    private const string Category = "Reliability";

    public static readonly DiagnosticDescriptor MissingErrorBoundary = new(
        id: "NTBA0001",
        title: "Component should be protected by ErrorBoundary",
        messageFormat: "Component '{0}' should be protected by ErrorBoundary. Wrapping '{1}' in an ErrorBoundary resolves this warning. Component to wrap: '{2}'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Blazor components in an interactive hierarchy should be protected by ErrorBoundary or a derived component at the root of their rendered content.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public static readonly DiagnosticDescriptor MissingTryCatch = new(
        id: "NTBA0002",
        title: "Component method should use try/catch",
        messageFormat: "Method '{0}' in interactive component '{1}' can be reached without try/catch handling",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Methods in interactive Blazor components without ErrorBoundary protection should be reached only through try/catch handling or delegate entirely to a safe member method.",
        customTags: [WellKnownDiagnosticTags.CompilationEnd]);

    public static readonly DiagnosticDescriptor LifecycleMissingTryCatch = new(
        id: "NTBA0003",
        title: "Lifecycle method should use try/catch",
        messageFormat: "Lifecycle method '{0}' in interactive component '{1}' should protect operational code with try/catch",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Interactive Blazor lifecycle methods should use try/catch around operational code to avoid unhandled circuit failures.");

    public static readonly DiagnosticDescriptor DisposeMissingTryCatch = new(
        id: "NTBA0004",
        title: "Dispose method should use try/catch",
        messageFormat: "Dispose method '{0}' in interactive component '{1}' should protect operational code with try/catch",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Interactive Blazor dispose methods should use try/catch around operational code to avoid unhandled cleanup failures.");

    public static readonly DiagnosticDescriptor JsInteropMissingTryCatch = new(
        id: "NTBA0005",
        title: "JS interop should use try/catch",
        messageFormat: "Method '{0}' in interactive component '{1}' performs JS interop without try/catch handling",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "JS interop calls in interactive Blazor components should be wrapped in try/catch so failures are handled intentionally.");

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
        title: "Layout ErrorBoundary should be keyed or reset",
        messageFormat: "Layout component '{0}' uses a long-lived ErrorBoundary. Key the boundary by route, reset it on navigation, or move it to page/widget scope.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "ErrorBoundary instances inside Blazor layouts can survive route changes. They should be keyed by route, reset on navigation, or moved to page/widget scope.");

    public static readonly DiagnosticDescriptor LayoutBoundaryUsesStaleRouteKey = new(
        id: "NTBA0011",
        title: "Layout ErrorBoundary route key should update on navigation",
        messageFormat: "Layout component '{0}' keys ErrorBoundary with a route snapshot that does not update on navigation. Use a computed route key or reset the boundary when the location changes.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A layout ErrorBoundary key must change when navigation changes. Snapshotting the route once during initialization leaves the boundary faulted across later route changes.");
}
