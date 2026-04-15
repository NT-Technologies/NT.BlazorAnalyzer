using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace NT.BlazorAnalyzer.Tests;

internal static class AnalyzerTestHarness
{
    public static async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(params SourceFile[] sources)
    {
        var compilation = CreateCompilation(sources);
        var analyzer = new BlazorErrorHandlingAnalyzer();
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(analyzer);
        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers);
        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

        return diagnostics
            .OrderBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Location.GetLineSpan().Path, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line)
            .ToArray();
    }

    private static CSharpCompilation CreateCompilation(IEnumerable<SourceFile> sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var syntaxTrees = sources.Select(source =>
            CSharpSyntaxTree.ParseText(
                text: SourceText.From(source.Text),
                options: parseOptions,
                path: source.Path));

        return CSharpCompilation.Create(
            assemblyName: "NT.BlazorAnalyzer.Tests.Generated",
            syntaxTrees: syntaxTrees,
            references: GetMetadataReferences(),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }

    private static IReadOnlyList<MetadataReference> GetMetadataReferences()
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
        AddReference(typeof(Microsoft.AspNetCore.Components.RenderModeAttribute).Assembly);
        AddReference(typeof(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder).Assembly);

        return references.Values.ToArray();

        void AddReference(Assembly assembly)
        {
            references[assembly.Location] = MetadataReference.CreateFromFile(assembly.Location);
        }
    }
}
