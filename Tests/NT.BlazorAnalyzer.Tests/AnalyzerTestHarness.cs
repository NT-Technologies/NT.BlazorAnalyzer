using Xunit;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace NT.BlazorAnalyzer.Tests;

internal static class AnalyzerTestHarness
{
    public static async Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(
        IEnumerable<SourceFile> sources,
        IEnumerable<AdditionalSourceFile>? additionalFiles = null,
        IReadOnlyDictionary<string, string>? globalOptions = null,
        bool concurrentAnalysis = true,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? treeOptions = null)
    {
        var compilation = CreateCompilation(sources);
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new BlazorErrorHandlingAnalyzer(), new BackgroundFailureAnalyzer());
        var analyzerOptions = new AnalyzerOptions(
            additionalFiles?
                .Select(static file => (AdditionalText)new InMemoryAdditionalText(file.Path, file.Text))
                .ToImmutableArray() ??
            ImmutableArray<AdditionalText>.Empty,
            new InMemoryAnalyzerConfigOptionsProvider(globalOptions ?? new Dictionary<string, string>(), treeOptions));
        var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(analyzerOptions, null, concurrentAnalysis, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false));
        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync();

        // An analyzer exception surfaces as AD0001; tests that filter by rule ID would otherwise hide a crash.
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "AD0001");

        return diagnostics
            .OrderBy(static diagnostic => diagnostic.Id, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Location.GetLineSpan().Path, StringComparer.Ordinal)
            .ThenBy(static diagnostic => diagnostic.Location.GetLineSpan().StartLinePosition.Line)
            .ToArray();
    }

    public static Task<IReadOnlyList<Diagnostic>> GetDiagnosticsAsync(params SourceFile[] sources) =>
        GetDiagnosticsAsync((IEnumerable<SourceFile>)sources, additionalFiles: null);

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
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
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
