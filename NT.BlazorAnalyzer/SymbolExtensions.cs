using Microsoft.CodeAnalysis;

namespace NT.BlazorAnalyzer;

internal static class SymbolExtensions
{
    public static bool InheritsFromOrEquals(this INamedTypeSymbol? symbol, INamedTypeSymbol target)
    {
        for (var current = symbol; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, target))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasPathContaining(this ISymbol symbol, string segment)
    {
        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            if (syntaxReference.SyntaxTree.FilePath.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        foreach (var location in symbol.Locations)
        {
            if (!location.IsInSource)
            {
                continue;
            }

            var path = location.GetLineSpan().Path;
            if (path.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }
}
