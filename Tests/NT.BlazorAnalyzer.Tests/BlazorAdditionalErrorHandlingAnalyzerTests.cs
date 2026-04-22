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
    public async Task LifecycleMethod_WithOnlyTrivialLocalStateMutation_DoesNotReportNtba0003()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "TrivialLifecycleComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    protected override void OnParametersSet()
                    {
                        currentCount++;
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0003");
    }

    [Fact]
    public async Task LifecycleMethod_DelegatingToSafeHelper_DoesNotReportNtba0003()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "DelegatedLifecycleComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private Logger Logger { get; } = new Logger();

                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                        => await RunSafelyAsync();

                    private async global::System.Threading.Tasks.Task RunSafelyAsync()
                    {
                        try
                        {
                            await LoadAsync();
                        }
                        catch (global::System.Exception ex)
                        {
                            Logger.LogError(ex);
                        }
                    }

                    private async global::System.Threading.Tasks.Task LoadAsync()
                    {
                        await global::System.Threading.Tasks.Task.Yield();
                        throw new global::System.InvalidOperationException();
                    }

                    private sealed class Logger
                    {
                        public void LogError(global::System.Exception ex)
                        {
                        }
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0003");
    }

    [Fact]
    public async Task LifecycleMethod_WithSwallowedCatch_StillReportsNtba0003()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "SwallowedLifecycleComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                    {
                        try
                        {
                            await LoadAsync();
                        }
                        catch (global::System.Exception)
                        {
                        }
                    }

                    private async global::System.Threading.Tasks.Task LoadAsync()
                    {
                        await global::System.Threading.Tasks.Task.CompletedTask;
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0003");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }

    [Fact]
    public async Task DisposeMethod_WithOnlyTrivialCleanup_DoesNotReportNtba0004()
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

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0004");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("Method 'Dispose'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeMethod_WithFailureProneHelperPath_ReportsNtba0004OnDispose()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "DisposeFailureComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    public void Dispose()
                    {
                        DisposeCore();
                    }

                    private void DisposeCore()
                    {
                        ThrowNow();
                    }

                    private void ThrowNow()
                    {
                        throw new global::System.InvalidOperationException();
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0004" && diagnostic.GetMessage().Contains("Dispose", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0004" && diagnostic.GetMessage().Contains("DisposeCore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisposeMethod_DelegatingToHandledHelper_DoesNotReportNtba0004()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "HandledDisposeComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private Logger Logger { get; } = new Logger();

                    public void Dispose() => DisposeCore();

                    private void DisposeCore()
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

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0004");
    }

    [Fact]
    public async Task DisposeMethod_WithSwallowedCatch_StillReportsNtba0004()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "SwallowedDisposeComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    public void Dispose()
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

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0004");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
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
    public async Task JsInteropWithSwallowedCatch_StillReportsNtba0005()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "SwallowedJsInteropComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                    private async global::System.Threading.Tasks.Task HandleClick()
                    {
                        try
                        {
                            await JS.InvokeVoidAsync("doSomething");
                        }
                        catch (global::System.Exception)
                        {
                        }
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0005");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }

    [Fact]
    public async Task JsInteropWithLoggingCatch_DoesNotReportNtba0005()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "LoggedJsInteropComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;
                    private Logger Logger { get; } = new Logger();

                    private async global::System.Threading.Tasks.Task HandleClick()
                    {
                        try
                        {
                            await JS.InvokeVoidAsync("doSomething");
                        }
                        catch (global::System.Exception ex)
                        {
                            Logger.LogError(ex);
                        }
                    }

                    private sealed class Logger
                    {
                        public void LogError(global::System.Exception ex)
                        {
                        }
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0005");
    }

    [Fact]
    public async Task MethodDelegatingToSafeJsHelper_DoesNotReportNtba0005()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "DelegatedJsInteropComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;
                    private Logger Logger { get; } = new Logger();

                    private global::System.Threading.Tasks.Task HandleClick() => InvokeJsSafelyAsync();

                    private async global::System.Threading.Tasks.Task InvokeJsSafelyAsync()
                    {
                        try
                        {
                            await JS.InvokeVoidAsync("doSomething");
                        }
                        catch (global::System.Exception ex)
                        {
                            Logger.LogError(ex);
                        }
                    }

                    private sealed class Logger
                    {
                        public void LogError(global::System.Exception ex)
                        {
                        }
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0005");
    }

    [Fact]
    public async Task DisposeAsyncCatchingJsDisconnectedException_DoesNotReportNtba0005()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "DisconnectedDisposeComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSObjectReference Module => default!;

                    public async global::System.Threading.Tasks.ValueTask DisposeAsync()
                    {
                        try
                        {
                            await Module.DisposeAsync();
                        }
                        catch (global::Microsoft.JSInterop.JSDisconnectedException)
                        {
                        }
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0005");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }

    [Fact]
    public async Task UnrelatedInvokeVoidAsyncMethod_DoesNotReportNtba0005()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "UnrelatedInvokeComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private Helper LocalHelper { get; } = new Helper();

                    private async global::System.Threading.Tasks.Task HandleClick()
                    {
                        await LocalHelper.InvokeVoidAsync("doSomething");
                    }

                    private sealed class Helper
                    {
                        public global::System.Threading.Tasks.Task InvokeVoidAsync(string identifier)
                            => global::System.Threading.Tasks.Task.CompletedTask;
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0005");
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

    [Fact]
    public async Task InteractiveDerivedErrorBoundaryWithBuiltInFallback_DoesNotReportNtba0001()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
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
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task InteractiveDerivedComponent_InheritsBoundaryProtectionFromWrappedBaseComponent()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "BaseSelect",
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
                    """),
            new SourceFile(
                Path: "Components/DerivedSelect.cs",
                Text: """
                    namespace TestComponents;

                    [DerivedSelect.__PrivateComponentRenderModeAttribute]
                    public class DerivedSelect : BaseSelect
                    {
                        private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                        {
                            public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode =>
                                global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer;
                        }
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("DerivedSelect", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InteractiveDerivedComponent_InheritsBoundaryProtectionFromWrappedGenericBaseComponent()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            new SourceFile(
                Path: "Components/GenericBaseSelect.razor.g.cs",
                Text: """
                    namespace TestComponents
                    {
                        public partial class GenericBaseSelect<TItem> : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
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
                            }
                        }
                    }
                    """),
            new SourceFile(
                Path: "Components/DerivedSelect.cs",
                Text: """
                    namespace TestComponents;

                    [DerivedSelect.__PrivateComponentRenderModeAttribute]
                    public class DerivedSelect : GenericBaseSelect<string>
                    {
                        private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                        {
                            public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode =>
                                global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer;
                        }
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("DerivedSelect", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InteractiveDerivedGenericComponent_SuggestsUnwrappedOwnerInsteadOfLeaf_WhenOtherOwnersAreWrapped()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            new SourceFile(
                Path: "Components/GenericBaseSelect.razor.g.cs",
                Text: """
                    namespace TestComponents
                    {
                        public partial class GenericBaseSelect<TItem> : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
                                __builder.OpenElement(0, "div");
                                __builder.CloseElement();
                            }
                        }
                    }
                    """),
            new SourceFile(
                Path: "Components/DerivedSelect.cs",
                Text: """
                    namespace TestComponents;

                    [DerivedSelect.__PrivateComponentRenderModeAttribute]
                    public class DerivedSelect : GenericBaseSelect<string>
                    {
                        private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                        {
                            public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode =>
                                global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer;
                        }
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "WrappedPage",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.DerivedSelect>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "UnwrappedPage",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.DerivedSelect>(0);
                    __builder.CloseComponent();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task SharedLeafComponent_WithOnlyInheritedRenderMode_ReportsWrapperComponentsInsteadOfLeaf()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "AssignmentForm",
                renderTreeStatements: """
                    __builder.OpenElement(0, "form");
                    __builder.CloseElement();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "EditAssignmentForm",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.AssignmentForm>(0);
                    __builder.AddAttribute(1, "OnValidSubmitCallback", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, UpdateAssignmentAsync));
                    __builder.AddAttribute(2, "FormButtons", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(3, "button");
                        __builder2.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void UpdateAssignmentAsync()
                    {
                        currentCount++;
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "NewAssignmentForm",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.AssignmentForm>(0);
                    __builder.AddAttribute(1, "OnValidSubmitCallback", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, CreateAssignmentAsync));
                    __builder.AddAttribute(2, "FormButtons", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(3, "button");
                        __builder2.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void CreateAssignmentAsync()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("component 'EditAssignmentForm'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("component 'NewAssignmentForm'", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("AssignmentForm'", StringComparison.Ordinal) && !diagnostic.GetMessage().Contains("EditAssignmentForm", StringComparison.Ordinal) && !diagnostic.GetMessage().Contains("NewAssignmentForm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SharedInteractiveLeafComponent_WithOnlyInheritedRenderMode_ReportsLeafAgainstWrapperOwners()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "FormHost",
                renderTreeStatements: """
                    __builder.OpenElement(0, "form");
                    __builder.CloseElement();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "DiaryNoteForm",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.FormHost>(0);
                    __builder.AddAttribute(1, "OnSubmit", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, SaveAsync));
                    __builder.AddAttribute(2, "FormButtons", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(3, "button");
                        __builder2.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void SaveAsync()
                    {
                        currentCount++;
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "EditDiaryNoteForm",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.DiaryNoteForm>(0);
                    __builder.AddAttribute(1, "OnValidSubmitCallback", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, UpdateDiaryNoteAsync));
                    __builder.AddAttribute(2, "FormButtons", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(3, "button");
                        __builder2.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void UpdateDiaryNoteAsync()
                    {
                        currentCount++;
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "NewDiaryNoteForm",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.DiaryNoteForm>(0);
                    __builder.AddAttribute(1, "OnValidSubmitCallback", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, CreateDiaryNoteAsync));
                    __builder.AddAttribute(2, "FormButtons", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(3, "button");
                        __builder2.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void CreateDiaryNoteAsync()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("component 'EditDiaryNoteForm'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("component 'NewDiaryNoteForm'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("component 'DiaryNoteForm'", StringComparison.Ordinal) && diagnostic.GetMessage().Contains("'EditDiaryNoteForm'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("component 'DiaryNoteForm'", StringComparison.Ordinal) && diagnostic.GetMessage().Contains("'NewDiaryNoteForm'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbstractInteractiveBaseComponent_DoesNotReportNtba0001_WhenConcreteDerivedComponentsAreOwnedByWrappedParent()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            new SourceFile(
                Path: "Components/ClaimContactListCard.razor.g.cs",
                Text: """
                    namespace TestComponents
                    {
                        [TestComponents.ClaimContactListCard.__PrivateComponentRenderModeAttribute]
                        public abstract partial class ClaimContactListCard : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
                                __builder.OpenElement(0, "div");
                                __builder.CloseElement();
                            }

                            private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                            {
                                public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode =>
                                    global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveAuto;
                            }
                        }
                    }
                    """),
            new SourceFile(
                Path: "Components/WitnessContactsCard.cs",
                Text: """
                    namespace TestComponents;

                    public sealed class WitnessContactsCard : ClaimContactListCard
                    {
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "ManagementContacts",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.WitnessContactsCard>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("ClaimContactListCard", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("WitnessContactsCard", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChildDiagnostic_DoesNotSuggestAbstractBaseResolver()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            new SourceFile(
                Path: "Components/AbstractOwner.razor.g.cs",
                Text: """
                    namespace TestComponents
                    {
                        [TestComponents.AbstractOwner.__PrivateComponentRenderModeAttribute]
                        public abstract partial class AbstractOwner : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
                                __builder.OpenComponent<global::TestComponents.Editor>(0);
                                __builder.AddAttribute(1, "Changed", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, HandleChanged));
                                __builder.CloseComponent();
                            }

                            private void HandleChanged()
                            {
                            }

                            private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                            {
                                public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode =>
                                    global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveAuto;
                            }
                        }
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Editor",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """),
            new SourceFile(
                Path: "Components/ConcreteDerivedOwner.cs",
                Text: """
                    namespace TestComponents;

                    public sealed class ConcreteDerivedOwner : AbstractOwner
                    {
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "OtherOwner",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.Editor>(0);
                    __builder.AddAttribute(1, "Changed", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, HandleChanged));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void HandleChanged()
                    {
                        currentCount++;
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("AbstractOwner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChildComponent_OwnedByAbstractBase_DoesNotReportWhenAllDerivedOwnersAreWrapped()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            new SourceFile(
                Path: "Components/AbstractOwner.razor.g.cs",
                Text: """
                    namespace TestComponents
                    {
                        [TestComponents.AbstractOwner.__PrivateComponentRenderModeAttribute]
                        public abstract partial class AbstractOwner : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
                                __builder.OpenComponent<global::TestComponents.Editor>(0);
                                __builder.AddAttribute(1, "Changed", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, HandleChanged));
                                __builder.CloseComponent();
                            }

                            private void HandleChanged()
                            {
                            }

                            private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                            {
                                public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode =>
                                    global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveAuto;
                            }
                        }
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Editor",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """),
            new SourceFile(
                Path: "Components/ConcreteDerivedOwnerA.cs",
                Text: """
                    namespace TestComponents;

                    public sealed class ConcreteDerivedOwnerA : AbstractOwner
                    {
                    }
                    """),
            new SourceFile(
                Path: "Components/ConcreteDerivedOwnerB.cs",
                Text: """
                    namespace TestComponents;

                    public sealed class ConcreteDerivedOwnerB : AbstractOwner
                    {
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "WrappedParentA",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.ConcreteDerivedOwnerA>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "WrappedParentB",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.ConcreteDerivedOwnerB>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Editor", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("AbstractOwner", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("ConcreteDerivedOwnerA", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("ConcreteDerivedOwnerB", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RelevantChildOwnedByAbstractBase_DoesNotReportWhenConcreteDerivedOwnersAreCoveredThroughWrappedAncestors()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            new SourceFile(
                Path: "Components/AbstractOwner.razor.g.cs",
                Text: """
                    namespace TestComponents
                    {
                        [TestComponents.AbstractOwner.__PrivateComponentRenderModeAttribute]
                        public abstract partial class AbstractOwner : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
                                __builder.OpenComponent<global::TestComponents.Editor>(0);
                                __builder.CloseComponent();
                            }

                            private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                            {
                                public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode =>
                                    global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveAuto;
                            }
                        }
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Editor",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private void HandleClick()
                    {
                        currentCount++;
                    }
                    """),
            new SourceFile(
                Path: "Components/ConcreteDerivedOwner.cs",
                Text: """
                    namespace TestComponents;

                    public sealed class ConcreteDerivedOwner : AbstractOwner
                    {
                    }
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Tab",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.ConcreteDerivedOwner>(0);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "WrappedPage",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Tab>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Editor", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("ConcreteDerivedOwner", StringComparison.Ordinal));
    }
}
