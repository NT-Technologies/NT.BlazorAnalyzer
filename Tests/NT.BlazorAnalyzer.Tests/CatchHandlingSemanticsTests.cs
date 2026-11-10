using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class CatchHandlingSemanticsTests
{
    [Theory]
    [InlineData("catch (global::System.Exception) { throw; }", true, false)]
    [InlineData("catch (global::System.Exception exception) { await DispatchExceptionAsync(exception); }", false, false)]
    [InlineData("catch (global::System.Exception exception) { global::System.Console.Error.WriteLine(exception); }", false, false)]
    [InlineData("catch (global::System.OperationCanceledException) { }", true, false)]
    [InlineData("catch (global::System.Threading.Tasks.TaskCanceledException) { }", true, false)]
    [InlineData("catch (global::System.Exception) when (global::System.Threading.CancellationToken.None.IsCancellationRequested) { }", true, false)]
    [InlineData("catch (global::Microsoft.JSInterop.JSDisconnectedException) { }", true, false)]
    [InlineData("catch (global::System.Exception) { }", false, true)]
    [InlineData("catch (global::System.Exception exception) { global::System.Console.Error.WriteLine(exception); throw; }", true, false)]
    [InlineData("catch (global::System.InvalidOperationException) { }", false, false)]
    public async Task LifecycleCatch_ReportsHandlingAndObservabilityIndependently(string catchClause, bool expectsNtba0003, bool expectsNtba0008)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "LifecycleComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: $$"""
                    private DataService Service { get; } = new();

                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                    {
                        try
                        {
                            await Service.LoadAsync();
                        }
                        {{catchClause}}
                    }

                    private sealed class DataService
                    {
                        public global::System.Threading.Tasks.Task LoadAsync() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data");
                    }
                    """));

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
        Assert.Equal(expectsNtba0008, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0008"));
    }

    [Fact]
    public async Task EventHandler_WithOnlyRethrowingCatch_ReportsNtba0002()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Counter",
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
                            throw new global::System.InvalidOperationException();
                        }
                        catch (global::System.Exception)
                        {
                            throw;
                        }
                    }
                    """));

        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "NTBA0002");
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "NTBA0008");
    }
}
