using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace NT.BlazorAnalyzer;

/// <summary>
/// Project-wide heuristics read from <c>.globalconfig</c> or the <c>[*.cs]</c> section of <c>.editorconfig</c>, plus the
/// per-compilation caches used to follow calls into the project's own source.
/// </summary>
internal sealed class AnalyzerSettings
{
    public const string FailureProneTypeSuffixesKey = "ntba.failure_prone_type_suffixes";
    public const string FailureProneMethodPrefixesKey = "ntba.failure_prone_method_prefixes";
    public const string SafeTypesKey = "ntba.safe_types";
    public const string FailureHandlingMethodsKey = "ntba.failure_handling_methods";
    public const string AssumeOwnerErrorBoundaryKey = "ntba.assume_owner_error_boundary";
    public const string UnknownAwaitsAreFailureProneKey = "ntba.unknown_awaits_are_failure_prone";

    private static readonly ImmutableArray<string> DefaultFailureProneTypeSuffixes =
        ["Repository", "Service", "Client", "Provider", "Manager", "Store", "Context"];

    private static readonly ImmutableArray<string> DefaultFailureProneMethodPrefixes =
    [
        "Get", "Load", "Save", "Search", "Export", "Import", "Open", "Download", "Upload", "Refresh",
        "Invoke", "Send", "Submit", "Create", "Update", "Patch", "Delete", "Remove", "Archive", "Restore"
    ];

    private static readonly ImmutableArray<string> DefaultSafeTypes =
    [
        "Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider",
        "Microsoft.AspNetCore.Components.Authorization.AuthenticationState",
        // Invoking a callback runs the owner's handler, which the owner's own diagnostics cover.
        "Microsoft.AspNetCore.Components.EventCallback"
    ];

    // Generic types are matched without type arguments, so "My.Result" matches My.Result<T>.
    private static readonly SymbolDisplayFormat TypeNameFormat = new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

    private readonly ConcurrentDictionary<IMethodSymbol, bool> followedCalls;
    private readonly Lazy<ImmutableArray<INamedTypeSymbol>> sourceTypes;

    private AnalyzerSettings(Compilation compilation, ImmutableArray<string> failureProneTypeSuffixes, ImmutableArray<string> failureProneMethodPrefixes, ImmutableArray<string> safeTypes, ImmutableArray<string> failureHandlingMethods, bool assumeOwnerErrorBoundary, bool unknownAwaitsAreFailureProne)
    {
        Compilation = compilation;
        FailureProneTypeSuffixes = failureProneTypeSuffixes;
        FailureProneMethodPrefixes = failureProneMethodPrefixes;
        SafeTypes = safeTypes.ToImmutableHashSet(StringComparer.Ordinal);
        FailureHandlingMethods = failureHandlingMethods.ToImmutableHashSet(StringComparer.Ordinal);
        AssumeOwnerErrorBoundary = assumeOwnerErrorBoundary;
        UnknownAwaitsAreFailureProne = unknownAwaitsAreFailureProne;
        DefaultRenderMode = IsStandaloneWebAssembly(compilation) ? "InteractiveWebAssembly" : null;
        FollowSourceCalls = true;
        followedCalls = new(SymbolEqualityComparer.Default);
        sourceTypes = new(() => [.. GetTypes(compilation.Assembly.GlobalNamespace)]);
    }

    private AnalyzerSettings(AnalyzerSettings source)
    {
        Compilation = source.Compilation;
        FailureProneTypeSuffixes = source.FailureProneTypeSuffixes;
        FailureProneMethodPrefixes = source.FailureProneMethodPrefixes;
        SafeTypes = source.SafeTypes;
        FailureHandlingMethods = source.FailureHandlingMethods;
        AssumeOwnerErrorBoundary = source.AssumeOwnerErrorBoundary;
        UnknownAwaitsAreFailureProne = source.UnknownAwaitsAreFailureProne;
        DefaultRenderMode = source.DefaultRenderMode;
        FollowSourceCalls = false;
        followedCalls = source.followedCalls;
        sourceTypes = source.sourceTypes;
    }

    public Compilation Compilation { get; }

    /// <summary>Type-name suffixes whose operational methods are treated as failure-prone. Replaces the defaults when set.</summary>
    public ImmutableArray<string> FailureProneTypeSuffixes { get; }

    /// <summary>Method-name prefixes treated as operational on failure-prone types. Replaces the defaults when set.</summary>
    public ImmutableArray<string> FailureProneMethodPrefixes { get; }

    /// <summary>Fully qualified types whose members never count as failure-prone. Adds to the defaults.</summary>
    public ImmutableHashSet<string> SafeTypes { get; }

    /// <summary>Method names that convert failures into values, such as Result <c>OnFailureAsync</c>; calls chained into them are safe.</summary>
    public ImmutableHashSet<string> FailureHandlingMethods { get; }

    /// <summary>
    /// Treats every component as rendered inside an owner ErrorBoundary. Defaults to true for component libraries,
    /// which have no routable or render-mode components and so cannot know who renders them.
    /// </summary>
    public bool AssumeOwnerErrorBoundary { get; }

    /// <summary>
    /// Strict mode: an awaited call with no evidence of failure, such as an unknown library method, counts as failure-prone.
    /// By default only evidence counts: JS interop, known I/O APIs, operational naming, or project code that does such work.
    /// </summary>
    public bool UnknownAwaitsAreFailureProne { get; }

    /// <summary>
    /// The render mode of components that declare none: InteractiveWebAssembly in a standalone WebAssembly app, where every
    /// component is interactive without <c>@rendermode</c>; otherwise null.
    /// </summary>
    public string? DefaultRenderMode { get; }

    /// <summary>False while analyzing a followed callee, so following stays one level deep and cannot cycle.</summary>
    public bool FollowSourceCalls { get; }

    public static AnalyzerSettings Create(AnalyzerConfigOptionsProvider optionsProvider, Compilation compilation)
    {
        string? Read(string key)
        {
            if (optionsProvider.GlobalOptions.TryGetValue(key, out var globalValue))
            {
                return globalValue;
            }

            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                if (optionsProvider.GetOptions(syntaxTree).TryGetValue(key, out var treeValue))
                {
                    return treeValue;
                }
            }

            return null;
        }

        return new AnalyzerSettings(
            compilation,
            Read(FailureProneTypeSuffixesKey) is { } suffixes ? Split(suffixes) : DefaultFailureProneTypeSuffixes,
            Read(FailureProneMethodPrefixesKey) is { } prefixes ? Split(prefixes) : DefaultFailureProneMethodPrefixes,
            DefaultSafeTypes.AddRange(Read(SafeTypesKey) is { } safeTypes ? Split(safeTypes) : []),
            Read(FailureHandlingMethodsKey) is { } methods ? Split(methods) : [],
            Read(AssumeOwnerErrorBoundaryKey)?.Trim() is { Length: > 0 } assumeOwner
                ? string.Equals(assumeOwner, "true", StringComparison.OrdinalIgnoreCase)
                : IsComponentLibrary(compilation),
            string.Equals(Read(UnknownAwaitsAreFailureProneKey)?.Trim(), "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Resolves a call to its body in this compilation: the method itself, or the single source implementation of an
    /// interface method. Returns null when the body is not in source or the target is ambiguous.
    /// </summary>
    public SyntaxNode? TryGetSourceBody(IMethodSymbol method, CancellationToken cancellationToken)
    {
        var target = method;
        if (method.ContainingType.TypeKind == TypeKind.Interface)
        {
            // A generic interface is implemented through a constructed form (IRepo<Foo> for IRepo<T>), so match by definition.
            var interfaceDefinition = method.ContainingType.OriginalDefinition;
            var implementations = sourceTypes.Value
                .SelectMany(type => type.AllInterfaces
                    .Where(implemented => SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, interfaceDefinition))
                    .SelectMany(implemented => implemented.GetMembers(method.Name).OfType<IMethodSymbol>())
                    .Where(member => SymbolEqualityComparer.Default.Equals(member.OriginalDefinition, method.OriginalDefinition))
                    .Select(member => type.FindImplementationForInterfaceMember(member) as IMethodSymbol))
                .Where(static implementation => implementation is not null)
                .Distinct(SymbolEqualityComparer.Default)
                .Take(2)
                .ToArray();
            if (implementations.Length != 1)
            {
                return null;
            }

            target = (IMethodSymbol)implementations[0]!;
        }

        foreach (var reference in target.OriginalDefinition.DeclaringSyntaxReferences)
        {
            switch (reference.GetSyntax(cancellationToken))
            {
                case Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax declaration:
                    return (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody?.Expression;
                case Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax localFunction:
                    return (SyntaxNode?)localFunction.Body ?? localFunction.ExpressionBody?.Expression;
            }
        }

        return null;
    }

    /// <summary>Caches a followed callee's verdict; the verdict depends only on the callee, so concurrent callers agree.</summary>
    public bool GetOrAddFollowedCall(IMethodSymbol method, Func<IMethodSymbol, bool> evaluate) =>
        followedCalls.GetOrAdd(method.OriginalDefinition, evaluate);

    /// <summary>Semantic model for a followed callee that lives in another syntax tree of this compilation.</summary>
    public SemanticModel GetSemanticModel(SyntaxTree syntaxTree) => Compilation.GetSemanticModel(syntaxTree);

    /// <summary>Settings for analyzing a followed callee: identical, but without further following.</summary>
    public AnalyzerSettings ForFollowedCallee() => new(this);

    /// <summary>True when the type, one of its base types, or one of its interfaces is listed in <see cref="SafeTypes"/>.</summary>
    public bool IsSafeType(ITypeSymbol? typeSymbol)
    {
        if (typeSymbol is null)
        {
            return false;
        }

        for (var current = typeSymbol; current is not null; current = current.BaseType)
        {
            if (SafeTypes.Contains(current.ToDisplayString(TypeNameFormat)))
            {
                return true;
            }
        }

        return typeSymbol.AllInterfaces.Any(interfaceType => SafeTypes.Contains(interfaceType.ToDisplayString(TypeNameFormat)));
    }

    /// <summary>
    /// True when a call returns, directly or through Task/ValueTask, a safe type or a Result type that carries failures as values.
    /// </summary>
    public bool IsSafeResultType(ITypeSymbol returnType)
    {
        var resultType = returnType is INamedTypeSymbol { TypeArguments.Length: 1 } awaitable &&
            awaitable.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks"
                ? awaitable.TypeArguments[0]
                : returnType;
        return IsSafeType(returnType) || IsSafeType(resultType) || CarriesFailureAsValue(resultType);
    }

    // The Result convention shared by NTResult, FluentResults, Ardalis.Result, ErrorOr and Refit's IApiResponse.
    // HttpResponseMessage.IsSuccessStatusCode is deliberately absent: HttpClient still throws on transport failures.
    private static bool CarriesFailureAsValue(ITypeSymbol type)
    {
        var candidates = type.AllInterfaces.Cast<ITypeSymbol>().Prepend(type);
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            candidates = candidates.Append(baseType);
        }

        return candidates.SelectMany(static candidate => candidate.GetMembers()).OfType<IPropertySymbol>().Any(static property =>
            property.Type.SpecialType == SpecialType.System_Boolean &&
            property.Name is "IsSuccess" or "IsSuccessful" or "Succeeded" or "IsFailure" or "IsFailed" or "HasFailed" or "IsError");
    }

    private static bool IsComponentLibrary(Compilation compilation)
    {
        if (compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.RouteAttribute") is not { } routeAttribute ||
            compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.RenderModeAttribute") is not { } renderModeAttribute)
        {
            return false;
        }

        return !GetTypes(compilation.Assembly.GlobalNamespace).Any(type => type.GetAttributes().Any(attribute =>
            attribute.AttributeClass is { } attributeClass &&
            (SymbolEqualityComparer.Default.Equals(attributeClass, routeAttribute) || attributeClass.InheritsFromOrEquals(renderModeAttribute))));
    }

    // A standalone WebAssembly app registers its own root components; a Blazor Web App client project does not.
    private static bool IsStandaloneWebAssembly(Compilation compilation) =>
        compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.WebAssembly.Hosting.WebAssemblyHostBuilder") is not null &&
        compilation.SyntaxTrees.Any(static tree => tree.GetText().ToString().IndexOf("RootComponents.Add", StringComparison.Ordinal) >= 0);

    private static IEnumerable<INamedTypeSymbol> GetTypes(INamespaceOrTypeSymbol container) =>
        container.GetMembers().SelectMany(member => member switch
        {
            INamespaceSymbol childNamespace => GetTypes(childNamespace),
            INamedTypeSymbol type => GetTypes(type).Prepend(type),
            _ => []
        });

    private static ImmutableArray<string> Split(string value) =>
        [.. value.Split([','], StringSplitOptions.RemoveEmptyEntries).Select(static item => item.Trim()).Where(static item => item.Length > 0)];
}
