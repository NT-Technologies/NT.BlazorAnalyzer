using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class BlazorCoverageRegressionTests
{
    [Fact]
    public async Task InteractiveComponent_DeclarationOnlyPartialMethod_IsIgnoredAsApiEntryPoint()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "PartialMethodComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        currentCount++;
                    }

                    partial void HelperDeclarationOnly();
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id),
            diagnostic =>
            {
                Assert.Equal("NTBA0002", diagnostic.Id);
                Assert.Contains("HandleClick", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task LifecycleMethod_WithOnlyNestedLocalFunction_DoesNotReportNtba0003()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "NestedLifecycleComponent",
                renderTreeStatements: """
                    var sequence = 0;
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(sequence++);
                    __builder.AddAttribute(sequence++, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(sequence++, "div");
                        __builder2.CloseElement();
                    }));
                    __builder.AddAttribute(sequence++, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(sequence++, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    protected override void OnParametersSet()
                    {
                        void Nested()
                        {
                            throw new global::System.InvalidOperationException();
                        }
                    }
                    """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task JsObjectReferenceDisposeAsync_ReportsNtba0004AndNtba0005()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "DisposeInteropComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSObjectReference Module => default!;

                    public async global::System.Threading.Tasks.ValueTask DisposeAsync()
                    {
                        await Module.DisposeAsync();
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0004");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0005");
    }

    [Fact]
    public async Task CatchCallingNonLoggingMethod_StillReportsNtba0008()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "ConsoleCatchComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        try
                        {
                            ThrowNow();
                        }
                        catch (global::System.Exception ex)
                        {
                            global::System.Console.WriteLine(ex.Message);
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
    public async Task CatchCallingILoggerMember_DoesNotReportNtba0008()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "LoggerCatchComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private global::Microsoft.Extensions.Logging.ILogger Logger => default!;

                    private void HandleClick()
                    {
                        try
                        {
                            ThrowNow();
                        }
                        catch (global::System.Exception)
                        {
                            Logger.BeginScope("scope");
                        }
                    }

                    private void ThrowNow()
                    {
                        throw new global::System.InvalidOperationException();
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }

    [Fact]
    public async Task JsInteropInsideUnrelatedIfGuard_StillReportsNtba0006()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "UnrelatedGuardComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;
                    private bool ready = true;

                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                    {
                        if (ready)
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
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0006");
    }

    [Fact]
    public async Task TryFinallyWithoutCatch_ReportsNtba0002()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "TryFinallyComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        try
                        {
                            currentCount++;
                        }
                        finally
                        {
                            currentCount--;
                        }
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("HandleClick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecursiveDelegation_IsNotTreatedAsSafe()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "RecursiveDelegationComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick() => HandleClickCore();

                    private void HandleClickCore() => HandleClick();
                    """));

        Assert.Equal(3, diagnostics.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("HandleClick", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("HandleClickCore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonGenericOpenComponentBoundary_IsNotAcceptedAsTypedBoundaryRoot()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "NonGenericBoundaryComponent",
                renderTreeStatements: """
                    __builder.OpenComponent(0, typeof(global::Microsoft.AspNetCore.Components.Web.ErrorBoundary));
                    __builder.CloseComponent();
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task BoundaryRootWithDirectNestedElement_IsRecognized()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "NestedBoundaryRootComponent",
                renderTreeStatements: """
                    var sequence = 0;
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(sequence++);
                    __builder.OpenElement(sequence++, "section");
                    __builder.CloseElement();
                    __builder.CloseComponent();
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0009", diagnostic.Id));
    }

    private static string CreateButtonRenderTree(string handlerName) =>
        $$"""
        __builder.OpenElement(0, "button");
        __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, {{handlerName}}));
        __builder.CloseElement();
        """;
}
