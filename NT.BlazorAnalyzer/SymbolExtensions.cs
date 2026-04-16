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

    public static Location? GetPreferredSourceLocation(this ISymbol symbol)
    {
        return symbol.Locations
            .Where(static location => location.IsInSource)
            .OrderBy(GetLocationRank)
            .ThenBy(static location => location.GetLineSpan().Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static location => location.GetLineSpan().StartLinePosition.Line)
            .FirstOrDefault();
    }

    public static string? TryGetRazorFilePath(this ISymbol symbol)
    {
        foreach (var location in symbol.Locations.Where(static location => location.IsInSource))
        {
            var path = location.GetLineSpan().Path;
            if (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var path = syntaxReference.SyntaxTree.FilePath;
            if (path.EndsWith(".razor.g.cs", StringComparison.OrdinalIgnoreCase))
            {
                return path.Substring(0, path.Length - 5);
            }
        }

        return null;
    }

    private static int GetLocationRank(Location location)
    {
        var path = location.GetLineSpan().Path;
        if (path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (path.EndsWith(".razor.cs", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (path.EndsWith(".razor.g.cs", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }
}
