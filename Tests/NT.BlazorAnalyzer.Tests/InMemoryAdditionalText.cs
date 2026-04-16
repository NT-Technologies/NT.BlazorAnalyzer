using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace NT.BlazorAnalyzer.Tests;

internal sealed class InMemoryAdditionalText : AdditionalText
{
    private readonly SourceText text;

    public InMemoryAdditionalText(string path, string content)
    {
        Path = path;
        text = SourceText.From(content);
    }

    public override string Path { get; }

    public override SourceText GetText(CancellationToken cancellationToken = default) => text;
}
