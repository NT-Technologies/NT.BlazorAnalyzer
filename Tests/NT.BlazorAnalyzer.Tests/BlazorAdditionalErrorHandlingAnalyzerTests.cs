using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class BlazorAdditionalErrorHandlingAnalyzerTests
{
    [Fact]
    public async Task LifecycleMethod_WithoutTryCatch_ReportsNtba0003()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "LifecycleComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                    {
                        await LoadAsync();
                    }

                    private async global::System.Threading.Tasks.Task LoadAsync()
                    {
                        await global::System.Threading.Tasks.Task.CompletedTask;
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0003");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("OnInitializedAsync", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeMethod_WithoutTryCatch_ReportsNtba0004()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "DisposeComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    public void Dispose()
                    {
                        Cleanup();
                    }

                    private void Cleanup()
                    {
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0004");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("Method 'Dispose'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JsInteropWithoutTryCatch_ReportsNtba0005()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "JsInteropComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                    private async global::System.Threading.Tasks.Task HandleClick()
                    {
                        await JS.InvokeVoidAsync("doSomething");
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0005");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("HandleClick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JsInteropInEarlyLifecycleWithoutGuard_ReportsNtba0006()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "JsLifecycleComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                    {
                        try
                        {
                            await JS.InvokeVoidAsync("doSomething");
                        }
                        catch (global::System.Exception)
                        {
                            throw;
                        }
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0006");
    }

    [Fact]
    public async Task JsInteropInEarlyLifecycleWithInteractivityGuard_DoesNotReportNtba0006()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "GuardedJsLifecycleComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;
                    private RenderInfo RendererInfo { get; } = new RenderInfo();

                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                    {
                        if (RendererInfo.IsInteractive)
                        {
                            try
                            {
                                await JS.InvokeVoidAsync("doSomething");
                            }
                            catch (global::System.Exception)
                            {
                                throw;
                            }
                        }
                    }

                    private sealed class RenderInfo
                    {
                        public bool IsInteractive { get; } = true;
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0006");
    }

    [Fact]
    public async Task AsyncVoidMethod_ReportsNtba0007()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "AsyncVoidComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private async void HandleClick()
                    {
                        await global::System.Threading.Tasks.Task.CompletedTask;
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0007");
    }

    [Fact]
    public async Task CatchWithoutLogging_ReportsNtba0008()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "CatchComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private void HandleClick()
                    {
                        try
                        {
                            ThrowNow();
                        }
                        catch (global::System.Exception)
                        {
                        }
                    }

                    private void ThrowNow()
                    {
                        throw new global::System.InvalidOperationException();
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }

    [Fact]
    public async Task CatchWithLogging_DoesNotReportNtba0008()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "LoggedCatchComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private Logger Logger { get; } = new Logger();

                    private void HandleClick()
                    {
                        try
                        {
                            ThrowNow();
                        }
                        catch (global::System.Exception ex)
                        {
                            Logger.LogError(ex);
                        }
                    }

                    private void ThrowNow()
                    {
                        throw new global::System.InvalidOperationException();
                    }

                    private sealed class Logger
                    {
                        public void LogError(global::System.Exception ex)
                        {
                        }
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }

    [Fact]
    public async Task RootErrorBoundaryWithoutErrorContent_ReportsNtba0009()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "BoundaryComponent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(2, "div");
                        __builder2.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0009");
    }

    [Fact]
    public async Task RootErrorBoundaryWithErrorContent_DoesNotReportNtba0009()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "BoundaryComponent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(2, "div");
                        __builder2.CloseElement();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0009");
    }

    [Fact]
    public async Task DerivedRootErrorBoundaryWithoutBuiltInFallback_ReportsNtba0009()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateCustomBoundary("CustomBoundary"),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "BoundaryComponent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.CustomBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(2, "div");
                        __builder2.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0009");
    }

    [Fact]
    public async Task DerivedRootErrorBoundaryWithBuiltInFallback_DoesNotReportNtba0009()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "CustomBoundary",
                baseType: "global::Microsoft.AspNetCore.Components.Web.ErrorBoundary",
                renderTreeStatements: """
                    if (CurrentException is null)
                    {
                        __builder.AddContent(0, ChildContent);
                    }
                    else
                    {
                        __builder.OpenElement(1, "p");
                        __builder.AddContent(2, "Fallback");
                        __builder.CloseElement();
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "BoundaryComponent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.CustomBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(2, "div");
                        __builder2.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0009");
    }
}
