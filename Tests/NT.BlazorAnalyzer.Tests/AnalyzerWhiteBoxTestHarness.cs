using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

internal sealed class SemanticTestContext
{
    public SemanticTestContext(CSharpCompilation compilation, SemanticModel semanticModel, CompilationUnitSyntax root)
    {
        Compilation = compilation;
        SemanticModel = semanticModel;
        Root = root;
    }

    public CSharpCompilation Compilation { get; }

    public SemanticModel SemanticModel { get; }

    public CompilationUnitSyntax Root { get; }

    public InvocationExpressionSyntax FindInvocation(string methodName, params string[] requiredFragments) =>
        FindInvocations(methodName, requiredFragments)
            .Single();

    public IEnumerable<InvocationExpressionSyntax> FindInvocations(string methodName, params string[] requiredFragments) =>
        Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Where(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var candidateName } &&
                string.Equals(candidateName, methodName, StringComparison.Ordinal) &&
                requiredFragments.All(fragment => invocation.ToString().Contains(fragment, StringComparison.Ordinal)));

    public ExpressionSyntax FindExpression(string text) =>
        Root.DescendantNodes()
            .OfType<ExpressionSyntax>()
            .Single(expression => string.Equals(expression.ToString(), text, StringComparison.Ordinal));
}

internal static class AnalyzerWhiteBoxTestHarness
{
    private const string DefaultUsings = """
        using System;
        using System.Threading.Tasks;
        using Microsoft.AspNetCore.Components;
        using Microsoft.AspNetCore.Components.Rendering;
        using Microsoft.AspNetCore.Components.Web;
        """;

    private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly Type AnalyzerType = typeof(BlazorErrorHandlingAnalyzer);
    private static readonly Type RazorAnalyzerType = typeof(RazorMarkupAnalyzer);

    public static SemanticTestContext CreateRenderTreeContext(
        string body,
        string extraMembers = "",
        string extraTypes = "")
    {
        var source = $$"""
            {{DefaultUsings}}

            namespace TestComponents;

            public sealed class CallbackChild : ComponentBase
            {
                [Parameter] public EventCallback OnSave { get; set; }
                [Parameter] public EventCallback<string> ValueChanged { get; set; }
                [Parameter] public Action? OnAction { get; set; }
                [Parameter] public RenderFragment? ChildContent { get; set; }
                [Parameter] public RenderFragment<string>? RowTemplate { get; set; }
            }

            public sealed class CustomBoundaryWithBuiltInErrorContent : ErrorBoundary
            {
                protected override void BuildRenderTree(RenderTreeBuilder builder)
                {
                    _ = CurrentException;
                    _ = ErrorContent;
                }
            }

            public sealed class TestComponent : ComponentBase
            {
                private string currentValue = string.Empty;
                private string dynamicAttributeName = string.Empty;
                private string dynamicElementName = string.Empty;

                private void HandleSave() { }

                private void HandleChange(string value)
                {
                    currentValue = value;
                }

                private object OtherMethod() => new();

                private static object CreateBinder() => new();

                {{extraMembers}}

                protected override void BuildRenderTree(RenderTreeBuilder __builder)
                {
                    {{body}}
                }
            }

            {{extraTypes}}
            """;

        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source), new CSharpParseOptions(LanguageVersion.Preview), "TestComponent.g.cs");
        var compilation = CSharpCompilation.Create(
            "NT.BlazorAnalyzer.Tests.WhiteBox",
            [tree],
            GetMetadataReferences(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var semanticModel = compilation.GetSemanticModel(tree);
        var root = tree.GetCompilationUnitRoot();
        return new SemanticTestContext(compilation, semanticModel, root);
    }

    public static object? InvokeAnalyzer(string methodName, params object?[] args) =>
        GetMethod(AnalyzerType, methodName, args.Length).Invoke(null, args);

    public static object? InvokeRazorAnalyzer(string methodName, params object?[] args) =>
        GetMethod(RazorAnalyzerType, methodName, args.Length).Invoke(null, args);

    public static MethodInfo GetAnalyzerMethod(string methodName, int parameterCount) =>
        GetMethod(AnalyzerType, methodName, parameterCount);

    public static MethodInfo GetRazorAnalyzerMethod(string methodName, int parameterCount) =>
        GetMethod(RazorAnalyzerType, methodName, parameterCount);

    public static T GetProperty<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return (T)property!.GetValue(instance)!;
    }

    public static object CreatePrivateInstance(string nestedTypeName, params object?[] args)
    {
        var type = AnalyzerType.GetNestedType(nestedTypeName, BindingFlags.NonPublic);
        Assert.NotNull(type);
        return Activator.CreateInstance(type!, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, binder: null, args, culture: null)!;
    }

    public static MethodInfo GetAnalyzerNestedMethod(string nestedTypeName, string methodName, int parameterCount)
    {
        var type = AnalyzerType.GetNestedType(nestedTypeName, BindingFlags.NonPublic);
        Assert.NotNull(type);
        return type!.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.Name == methodName && method.GetParameters().Length == parameterCount);
    }

    public static INamedTypeSymbol GetRequiredType(this SemanticTestContext context, string metadataName)
    {
        var symbol = context.Compilation.GetTypeByMetadataName(metadataName);
        Assert.NotNull(symbol);
        return symbol!;
    }

    public static Location CreateLocation(string path, string text, string token)
    {
        var spanStart = text.IndexOf(token, StringComparison.Ordinal);
        Assert.True(spanStart >= 0, $"Token '{token}' was not found.");
        var span = new TextSpan(spanStart, token.Length);
        var sourceText = SourceText.From(text);
        return Location.Create(path, span, sourceText.Lines.GetLinePositionSpan(span));
    }

    private static MethodInfo GetMethod(Type type, string methodName, int parameterCount) =>
        type.GetMethods(StaticFlags)
            .Single(method => method.Name == methodName && method.GetParameters().Length == parameterCount);

    private static ImmutableArray<MetadataReference> GetMetadataReferences()
    {
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        var trustedPlatformAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (!string.IsNullOrWhiteSpace(trustedPlatformAssemblies))
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                references[path] = MetadataReference.CreateFromFile(path);
            }
        }

        AddReference(typeof(object).Assembly);
        AddReference(typeof(Enumerable).Assembly);
        AddReference(typeof(Task).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.ComponentBase).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.Web.ErrorBoundary).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder).Assembly);

        return [.. references.Values];

        void AddReference(Assembly assembly)
        {
            references[assembly.Location] = MetadataReference.CreateFromFile(assembly.Location);
        }
    }
}
