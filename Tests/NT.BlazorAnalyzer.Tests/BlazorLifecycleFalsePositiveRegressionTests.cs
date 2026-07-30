using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class BlazorLifecycleFalsePositiveRegressionTests
{
    [Fact]
    public async Task LifecycleMethod_WithDelegatedParameterValidation_DoesNotReportNtba0003()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "DelegatedValidationComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    [global::Microsoft.AspNetCore.Components.Parameter]
                    public string? Value { get; set; }

                    protected override void OnParametersSet()
                    {
                        base.OnParametersSet();
                        ValidateParameters();
                    }

                    private void ValidateParameters()
                    {
                        if (Value is null)
                        {
                            throw new global::System.ArgumentNullException(nameof(Value));
                        }
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0003");
    }

    [Fact]
    public async Task LifecycleMethod_ResolvingCultureInfo_DoesNotReportNtba0003()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "CultureComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    [global::Microsoft.AspNetCore.Components.Parameter]
                    public string CultureCode { get; set; } = "en-US";

                    private global::System.Globalization.CultureInfo Culture { get; set; } = default!;

                    protected override void OnParametersSet()
                    {
                        base.OnParametersSet();
                        Culture = global::System.Globalization.CultureInfo.GetCultureInfo(CultureCode);
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0003");
    }

    [Fact]
    public async Task OnAfterRenderAsync_CatchingJsDisconnectedException_DoesNotReportLifecycleOrSwallowedCatchDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "DisconnectedRenderComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                    protected override async global::System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
                    {
                        await base.OnAfterRenderAsync(firstRender);
                        try
                        {
                            if (firstRender)
                            {
                                await JS.InvokeVoidAsync("initialize");
                            }
                        }
                        catch (global::Microsoft.JSInterop.JSDisconnectedException)
                        {
                        }
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0003");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }

    [Fact]
    public async Task OnAfterRenderAsync_CatchingDisconnectFromJsRuntimeImportExtension_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "ImportExtensionComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                    protected override async global::System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
                    {
                        try
                        {
                            await JS.ImportModuleAsync();
                        }
                        catch (global::Microsoft.JSInterop.JSDisconnectedException)
                        {
                        }
                    }
                    """),
            new SourceFile(
                "JsRuntimeExtensions.cs",
                """
                namespace TestComponents;

                internal static class JsRuntimeExtensions
                {
                    public static global::System.Threading.Tasks.ValueTask<global::Microsoft.JSInterop.IJSObjectReference> ImportModuleAsync(this global::Microsoft.JSInterop.IJSRuntime jsRuntime) =>
                        new(default(global::Microsoft.JSInterop.IJSObjectReference)!);
                }
                """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "NTBA0003" or "NTBA0005" or "NTBA0008");
    }

    [Fact]
    public async Task JsDisconnectedCatch_DoesNotHandleUnrelatedLifecycleFailure()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "PartiallyHandledRenderComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;
                    private DataService Service { get; } = new();

                    protected override async global::System.Threading.Tasks.Task OnAfterRenderAsync(bool firstRender)
                    {
                        try
                        {
                            await Service.LoadAsync();
                            await JS.InvokeVoidAsync("initialize");
                        }
                        catch (global::Microsoft.JSInterop.JSDisconnectedException)
                        {
                        }
                    }

                    private sealed class DataService
                    {
                        public global::System.Threading.Tasks.Task LoadAsync() => global::System.Threading.Tasks.Task.CompletedTask;
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0003");
    }

    [Fact]
    public async Task DisposeAsync_WithFinallyCleanupAndExpectedDisconnectHandling_DoesNotReportNtba0004()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "GuardedDisposeComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSObjectReference? Module { get; set; }

                    public async global::System.Threading.Tasks.ValueTask DisposeAsync()
                    {
                        try
                        {
                            await DisposeAsyncCore();
                        }
                        finally
                        {
                            Module = null;
                            global::System.GC.SuppressFinalize(this);
                        }
                    }

                    private async global::System.Threading.Tasks.ValueTask DisposeAsyncCore()
                    {
                        if (Module is null)
                        {
                            return;
                        }

                        try
                        {
                            await Module.InvokeVoidAsync("dispose");
                            await Module.DisposeAsync();
                        }
                        catch (global::Microsoft.JSInterop.JSDisconnectedException)
                        {
                        }
                        finally
                        {
                            Module = null;
                        }
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0004");
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }
}
