using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class CustomBoundaryTests
{
    private static readonly SourceFile AppBoundary = new(
        "Boundaries/AppBoundary.cs",
        """
        namespace TestComponents;

        public class AppBoundary : global::Microsoft.AspNetCore.Components.ErrorBoundaryBase
        {
            protected override global::System.Threading.Tasks.Task OnErrorAsync(global::System.Exception exception) => global::System.Threading.Tasks.Task.CompletedTask;
        }
        """);

    [Fact]
    public async Task ChildInsideBoundaryNestedInAnotherComponentsContent_IsCovered()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources: [
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Parent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.Section>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) => {
                        __builder2.OpenElement(2, "div");
                        __builder2.OpenComponent<global::TestComponents.AppBoundary>(3);
                        __builder2.AddAttribute(4, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder3) => {
                            foreach (var item in new[] { 1, 2 }) {
                                __builder3.OpenComponent<global::TestComponents.Child>(5);
                                __builder3.AddComponentParameter(6, "Value", item);
                                __builder3.SetKey(item);
                                __builder3.CloseComponent();
                            }
                        }));
                        __builder2.CloseComponent();
                        __builder2.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Child",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    [global::Microsoft.AspNetCore.Components.Parameter] public int Value { get; set; }

                    private void HandleClick() => throw new global::System.InvalidOperationException();
                    """),
            AppBoundary,
            new SourceFile("Components/Section.cs", """
                namespace TestComponents;

                public class Section : global::Microsoft.AspNetCore.Components.ComponentBase
                {
                    [global::Microsoft.AspNetCore.Components.Parameter] public global::Microsoft.AspNetCore.Components.RenderFragment? ChildContent { get; set; }
                }
                """)],
            additionalFiles: [
                TestComponentSources.CreateRazorMarkup("Parent", """
                    @rendermode InteractiveServer
                    <Section>
                        <div>
                            <AppBoundary>
                                @foreach (var item in new[] { 1, 2 }) {
                                    <Child @key="item" Value="item" />
                                }
                            </AppBoundary>
                        </div>
                    </Section>
                    """)
            ]);

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("""
        __builder2.OpenComponent<global::TestComponents.Child>(2);
        __builder2.CloseComponent();
        """)]
    [InlineData("""
        foreach (var item in new[] { 1, 2 })
        {
            __builder2.OpenComponent<global::TestComponents.Child>(2);
            __builder2.CloseComponent();
        }
        """)]
    public async Task ChildRenderedInsideErrorBoundaryBaseImplementation_IsCovered(string childContent)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Parent",
                renderTreeStatements: $$"""
                    __builder.OpenComponent<global::TestComponents.AppBoundary>(0);
                    __builder.AddComponentParameter(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                    {{childContent}}
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Child",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private void HandleClick() => throw new global::System.InvalidOperationException();
                    """),
            AppBoundary);

        Assert.Empty(diagnostics);
    }
}
