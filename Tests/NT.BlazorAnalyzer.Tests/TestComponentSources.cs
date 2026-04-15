namespace NT.BlazorAnalyzer.Tests;

internal static class TestComponentSources
{
    public static SourceFile CreateInteractiveComponent(
        string componentName,
        string renderTreeStatements,
        string? razorMethods = null,
        string baseType = "global::Microsoft.AspNetCore.Components.ComponentBase",
        string @namespace = "TestComponents") =>
        CreateComponent(componentName, renderTreeStatements, razorMethods, baseType, @namespace, interactive: true);

    public static SourceFile CreateStaticComponent(
        string componentName,
        string renderTreeStatements,
        string? razorMethods = null,
        string baseType = "global::Microsoft.AspNetCore.Components.ComponentBase",
        string @namespace = "TestComponents") =>
        CreateComponent(componentName, renderTreeStatements, razorMethods, baseType, @namespace, interactive: false);

    public static SourceFile CreateCodeBehind(
        string componentName,
        string methods,
        string @namespace = "TestComponents") =>
        new(
            Path: $"Components/{componentName}.razor.cs",
            Text: $$"""
                namespace {{@namespace}};

                public partial class {{componentName}}
                {
                {{Indent(methods, 1)}}
                }
                """);

    public static SourceFile CreateCustomBoundary(
        string boundaryName,
        string @namespace = "TestComponents") =>
        new(
            Path: $"Components/{boundaryName}.cs",
            Text: $$"""
                namespace {{@namespace}};

                public class {{boundaryName}} : global::Microsoft.AspNetCore.Components.Web.ErrorBoundary
                {
                }
                """);

    private static SourceFile CreateComponent(
        string componentName,
        string renderTreeStatements,
        string? razorMethods,
        string baseType,
        string @namespace,
        bool interactive)
    {
        var attribute = interactive
            ? $"[{@namespace}.{componentName}.__PrivateComponentRenderModeAttribute]{Environment.NewLine}"
            : string.Empty;

        var renderModeAttribute = interactive
            ? $$"""

                    private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                    {
                        public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode =>
                            global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer;
                    }
                """
            : string.Empty;

        var methods = string.IsNullOrWhiteSpace(razorMethods)
            ? string.Empty
            : $$"""

                #line 100 "Components/{{componentName}}.razor"
                {{Indent(razorMethods, 2)}}
                #line default
                #line hidden
                """;

        return new SourceFile(
            Path: $"Components/{componentName}.razor.g.cs",
            Text: $$"""
                namespace {{@namespace}}
                {
                    {{attribute}}public partial class {{componentName}} : {{baseType}}
                    {
                        protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                        {
                {{Indent(renderTreeStatements, 3)}}
                        }
                {{methods}}{{renderModeAttribute}}
                    }
                }
                """);
    }

    private static string Indent(string value, int level)
    {
        var indentation = new string(' ', level * 4);
        return string.Join(Environment.NewLine, value.Split(Environment.NewLine).Select(line => indentation + line));
    }
}
