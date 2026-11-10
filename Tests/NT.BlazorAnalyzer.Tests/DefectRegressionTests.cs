using Xunit;

namespace NT.BlazorAnalyzer.Tests;

/// <summary>
/// Defects found by the resilience and white-box audits, each proven red before its fix.
/// </summary>
public sealed class DefectRegressionTests
{
    private const string ButtonRenderTree = """
        __builder.OpenElement(0, "button");
        __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
        __builder.CloseElement();
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildRenderTreeOverload_DoesNotReplaceTheRenderedRegions(bool overloadFirst)
    {
        const string overload = "protected void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder, int unused) { }";
        var methods = overloadFirst
            ? overload + "\nprivate void HandleClick() { }"
            : "private void HandleClick() { }\n" + overload;
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [TestComponentSources.CreateInteractiveComponent(componentName: "Counter", renderTreeStatements: ButtonRenderTree, razorMethods: methods)],
            concurrentAnalysis: false);

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task FunctionPointerCall_DoesNotCrashTheAnalyzer()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Pointer",
                renderTreeStatements: ButtonRenderTree,
                razorMethods: """
                    private unsafe void HandleClick()
                    {
                        delegate*<void> callback = &Noop;
                        callback();
                    }

                    private static void Noop()
                    {
                    }
                    """));

        // The harness fails on any AD0001; reaching the rule check proves the analyzer completed.
        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task EventInAppNamespaceStartingWithSystem_IsAnExternalCallback()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Listener",
                renderTreeStatements: "",
                razorMethods: """
                    private global::Systematic.Events.Bus Bus { get; } = new();

                    private void Start() => Bus.Changed += (_, _) => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test").Wait();
                    """),
            new SourceFile("Bus.cs", """
                namespace Systematic.Events;

                public sealed class Bus
                {
                    public event global::System.EventHandler? Changed;
                }
                """));

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "NTBA0012");
    }

    [Fact]
    public async Task TargetTypedTimerCreation_IsAnExternalCallback()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Poller",
                renderTreeStatements: "",
                razorMethods: """
                    private global::System.Threading.Timer? timer;

                    private void Start() => timer = new(_ => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test").Wait(), null, 0, 1000);
                    """));

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "NTBA0012");
    }

    [Theory]
    [InlineData("_error = string.Empty;")]
    [InlineData("_error = \"\";")]
    public async Task CatchThatOnlyResetsStringState_IsStillSilent(string catchBody)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Loader",
                renderTreeStatements: "",
                razorMethods: $$"""
                    private string? _error;

                    private void Load()
                    {
                        try
                        {
                            _error = null;
                        }
                        catch (global::System.Exception)
                        {
                            {{catchBody}}
                        }
                    }
                    """));

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "NTBA0008");
    }
}
