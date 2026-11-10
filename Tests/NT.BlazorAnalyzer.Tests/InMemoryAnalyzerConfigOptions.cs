using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NT.BlazorAnalyzer.Tests;

internal sealed class InMemoryAnalyzerConfigOptions(IReadOnlyDictionary<string, string> _values) : AnalyzerConfigOptions
{
    public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value) => _values.TryGetValue(key, out value);
}
