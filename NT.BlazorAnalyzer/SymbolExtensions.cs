using Microsoft.CodeAnalysis;

namespace NT.BlazorAnalyzer;

internal static class SymbolExtensions
{
    public static string? TryGetSourcePath(this Location? location)
    {
        if (location is null || !location.IsInSource)
        {
            return null;
        }

        try
        {
            return location.GetLineSpan().Path;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static int? TryGetStartLine(this Location? location)
    {
        if (location is null || !location.IsInSource)
        {
            return null;
        }

        try
        {
            return location.GetLineSpan().StartLinePosition.Line;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

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

            var path = location.TryGetSourcePath();
            if (path is not null && path.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public static Location? GetPreferredSourceLocation(this ISymbol symbol)
    {
        var preferredLocation = symbol.Locations
            .Where(static location => location.IsInSource)
            .OrderBy(GetLocationRank)
            .ThenBy(static location => location.TryGetSourcePath(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static location => location.TryGetStartLine() ?? int.MaxValue)
            .FirstOrDefault();

        return symbol.PreferNonGeneratedSourceLocation(preferredLocation);
    }

    public static Location? PreferNonGeneratedSourceLocation(this ISymbol symbol, Location? location)
    {
        return TryMapGeneratedRazorLocation(symbol, location) ?? location;
    }

    public static string? TryGetRazorFilePath(this ISymbol symbol)
    {
        foreach (var location in symbol.Locations.Where(static location => location.IsInSource))
        {
            var path = location.TryGetSourcePath();
            if (path?.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) == true)
            {
                return path;
            }
        }

        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            var path = syntaxReference.SyntaxTree.FilePath;
            if (TryMapGeneratedRazorPath(path) is { } razorPath)
            {
                return razorPath;
            }
        }

        return null;
    }

    private static int GetLocationRank(Location location)
    {
        var path = location.TryGetSourcePath();
        if (path is null)
        {
            return 4;
        }

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

    private static Location? TryMapGeneratedRazorLocation(ISymbol symbol, Location? location)
    {
        if (location is null || !location.IsInSource)
        {
            return location;
        }

        var path = location.TryGetSourcePath();
        if (path?.EndsWith(".razor.g.cs", StringComparison.OrdinalIgnoreCase) != true)
        {
            return location;
        }

        return GetPreferredDeclaredSourceLocation(symbol) ?? location;
    }

    private static string? TryMapGeneratedRazorPath(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (path.EndsWith(".razor.g.cs", StringComparison.OrdinalIgnoreCase))
        {
            return path.Substring(0, path.Length - 5);
        }

        if (path.EndsWith("_razor.g.cs", StringComparison.OrdinalIgnoreCase))
        {
            var mappedPath = path.Substring(0, path.Length - "_razor.g.cs".Length) + ".razor";
            const string sourceGeneratorSegment = "RazorSourceGenerator\\";
            var sourceGeneratorIndex = mappedPath.LastIndexOf(sourceGeneratorSegment, StringComparison.OrdinalIgnoreCase);
            if (sourceGeneratorIndex >= 0)
            {
                return mappedPath.Substring(sourceGeneratorIndex + sourceGeneratorSegment.Length);
            }

            return mappedPath;
        }

        return null;
    }

    private static Location? GetPreferredDeclaredSourceLocation(ISymbol symbol)
    {
        return symbol.Locations
            .Where(static candidate => candidate.IsInSource)
            .Where(static candidate => candidate.TryGetSourcePath()?.EndsWith(".razor.g.cs", StringComparison.OrdinalIgnoreCase) != true)
            .OrderBy(GetLocationRank)
            .ThenBy(static candidate => candidate.TryGetSourcePath(), StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.TryGetStartLine() ?? int.MaxValue)
            .FirstOrDefault();
    }
}
