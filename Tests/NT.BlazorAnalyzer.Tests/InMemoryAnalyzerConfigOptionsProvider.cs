using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NT.BlazorAnalyzer.Tests;

/// <summary>
/// Global options model a .globalconfig; tree options model the <c>[*.cs]</c> section of an .editorconfig, keyed by source path.
/// </summary>
internal sealed class InMemoryAnalyzerConfigOptionsProvider(IReadOnlyDictionary<string, string> _globalValues, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? _treeValues = null) : AnalyzerConfigOptionsProvider
{
    private static readonly AnalyzerConfigOptions Empty = new InMemoryAnalyzerConfigOptions(new Dictionary<string, string>());

    public override AnalyzerConfigOptions GlobalOptions { get; } = new InMemoryAnalyzerConfigOptions(_globalValues);

    public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) =>
        _treeValues is not null && _treeValues.TryGetValue(tree.FilePath, out var values) ? new InMemoryAnalyzerConfigOptions(values) : Empty;

    public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => Empty;
}
