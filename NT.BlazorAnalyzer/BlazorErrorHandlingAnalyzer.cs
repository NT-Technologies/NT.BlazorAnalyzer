using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace NT.BlazorAnalyzer;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlazorErrorHandlingAnalyzer : DiagnosticAnalyzer
{
    private const string StaticRenderModeKey = "<static>";

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
    [
        DiagnosticDescriptors.MissingErrorBoundary,
        DiagnosticDescriptors.MissingTryCatch,
        DiagnosticDescriptors.LifecycleMissingTryCatch,
        DiagnosticDescriptors.DisposeMissingTryCatch,
        DiagnosticDescriptors.JsInteropMissingTryCatch,
        DiagnosticDescriptors.JsInteropRequiresInteractivityGuard,
        DiagnosticDescriptors.AsyncVoidMethod,
        DiagnosticDescriptors.CatchWithoutLogging,
        DiagnosticDescriptors.ErrorBoundaryMissingErrorContent,
        DiagnosticDescriptors.LayoutBoundaryShouldBeRouteKeyed,
        DiagnosticDescriptors.LayoutBoundaryUsesStaleRouteKey
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterCompilationStartAction(static compilationStartContext =>
        {
            var componentBaseSymbol = compilationStartContext.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.ComponentBase");
            var layoutComponentBaseSymbol = compilationStartContext.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.LayoutComponentBase");
            var errorBoundarySymbol = compilationStartContext.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.Web.ErrorBoundary");
            var renderModeAttributeSymbol = compilationStartContext.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.RenderModeAttribute");

            if (componentBaseSymbol is null || layoutComponentBaseSymbol is null || errorBoundarySymbol is null || renderModeAttributeSymbol is null)
            {
                return;
            }

            var interactiveComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var allComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var layoutComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var declaredRenderModes = new ConcurrentDictionary<INamedTypeSymbol, string?>(SymbolEqualityComparer.Default);
            var localBoundaryComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var rootBoundaryKeyedComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var localRelevantBoundaryComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var boundaryWithErrorContentComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var boundaryComponentsWithBuiltInErrorContent = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var rootBoundaryComponents = new ConcurrentDictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var componentOwners = new ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>>(SymbolEqualityComparer.Default);
            var buildRenderTreeRootMethods = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            var methodAnalyses = new ConcurrentDictionary<IMethodSymbol, MethodAnalysis>(SymbolEqualityComparer.Default);
            var boundaryComponentNames = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var componentDiagnosticLocations = new ConcurrentDictionary<INamedTypeSymbol, ComponentDiagnosticLocations>(SymbolEqualityComparer.Default);
            var locallyReportedMissingErrorBoundaryComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var locallyReportedBoundaryMissingErrorContentComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var locallyReportedMissingTryCatchMethods = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            var razorAdditionalFiles = CreateRazorAdditionalFileMap(compilationStartContext.Options.AdditionalFiles);
            var razorMarkupCache = new ConcurrentDictionary<string, RazorMarkupAnalysis?>(StringComparer.OrdinalIgnoreCase);
            var boundaryCoverageResolver = new BoundaryCoverageResolver(
                compilationStartContext.Compilation,
                componentBaseSymbol,
                errorBoundarySymbol,
                renderModeAttributeSymbol,
                razorAdditionalFiles,
                razorMarkupCache);

            compilationStartContext.RegisterSymbolAction(
                symbolContext => CollectCandidateComponent(
                    symbolContext,
                    componentBaseSymbol,
                    layoutComponentBaseSymbol,
                    errorBoundarySymbol,
                    renderModeAttributeSymbol,
                    interactiveComponents,
                    allComponents,
                    layoutComponents,
                    declaredRenderModes,
                    boundaryComponentNames),
                SymbolKind.NamedType);

            compilationStartContext.RegisterSymbolStartAction(symbolStartContext =>
            {
                if (symbolStartContext.Symbol is not INamedTypeSymbol componentSymbol ||
                    !IsComponent(componentSymbol, componentBaseSymbol))
                {
                    return;
                }

                var localBuildRenderTreeRootMethods = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
                var localMethodAnalyses = new ConcurrentDictionary<IMethodSymbol, MethodAnalysis>(SymbolEqualityComparer.Default);

                symbolStartContext.RegisterSyntaxNodeAction(
                    syntaxContext => CollectLocalMissingTryCatchAnalysis(
                        syntaxContext,
                        componentBaseSymbol,
                        componentSymbol,
                        localBuildRenderTreeRootMethods,
                        localMethodAnalyses),
                    SyntaxKind.MethodDeclaration);

                symbolStartContext.RegisterSymbolEndAction(symbolEndContext =>
                    ReportLocalMissingTryCatchDiagnostics(
                        symbolEndContext,
                        componentSymbol,
                        localBuildRenderTreeRootMethods,
                        localMethodAnalyses,
                        locallyReportedMissingTryCatchMethods,
                        boundaryCoverageResolver));
            }, SymbolKind.NamedType);

            compilationStartContext.RegisterSyntaxNodeAction(
                syntaxContext => CollectMethodAnalysis(
                    syntaxContext,
                    componentBaseSymbol,
                    layoutComponentBaseSymbol,
                    errorBoundarySymbol,
                    renderModeAttributeSymbol,
                    allComponents,
                    localBoundaryComponents,
                    localRelevantBoundaryComponents,
                    boundaryWithErrorContentComponents,
                    boundaryComponentsWithBuiltInErrorContent,
                    rootBoundaryComponents,
                    rootBoundaryKeyedComponents,
                    componentOwners,
                    buildRenderTreeRootMethods,
                    methodAnalyses,
                    boundaryComponentNames,
                    componentDiagnosticLocations,
                    locallyReportedMissingErrorBoundaryComponents,
                    locallyReportedBoundaryMissingErrorContentComponents,
                    razorAdditionalFiles,
                    razorMarkupCache),
                SyntaxKind.MethodDeclaration);

            compilationStartContext.RegisterCompilationEndAction(
                compilationEndContext => AnalyzeCompilationEnd(
                    compilationEndContext,
                    allComponents,
                    declaredRenderModes,
                    interactiveComponents,
                    localBoundaryComponents,
                    localRelevantBoundaryComponents,
                    boundaryWithErrorContentComponents,
                    boundaryComponentsWithBuiltInErrorContent,
                    rootBoundaryComponents,
                    rootBoundaryKeyedComponents,
                    layoutComponents,
                    componentOwners,
                    buildRenderTreeRootMethods,
                    methodAnalyses,
                    componentDiagnosticLocations,
                    locallyReportedMissingErrorBoundaryComponents,
                    locallyReportedBoundaryMissingErrorContentComponents,
                    locallyReportedMissingTryCatchMethods));
        });
    }

    private static void CollectCandidateComponent(
        SymbolAnalysisContext context,
        INamedTypeSymbol componentBaseSymbol,
        INamedTypeSymbol layoutComponentBaseSymbol,
        INamedTypeSymbol errorBoundarySymbol,
        INamedTypeSymbol renderModeAttributeSymbol,
        ConcurrentDictionary<INamedTypeSymbol, byte> interactiveComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> allComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> layoutComponents,
        ConcurrentDictionary<INamedTypeSymbol, string?> declaredRenderModes,
        ConcurrentDictionary<string, byte> boundaryComponentNames)
    {
        var namedType = (INamedTypeSymbol)context.Symbol;
        if (namedType.TypeKind == TypeKind.Class && namedType.InheritsFromOrEquals(errorBoundarySymbol))
        {
            boundaryComponentNames.TryAdd(namedType.Name, 0);
        }

        if (!IsComponent(namedType, componentBaseSymbol))
        {
            return;
        }

        allComponents.TryAdd(namedType, 0);
        if (namedType.InheritsFromOrEquals(layoutComponentBaseSymbol))
        {
            layoutComponents.TryAdd(namedType, 0);
        }

        var declaredRenderMode = GetDeclaredRenderModeKey(namedType, renderModeAttributeSymbol);
        declaredRenderModes.TryAdd(namedType, declaredRenderMode);
        if (declaredRenderMode is not null)
        {
            interactiveComponents.TryAdd(namedType, 0);
        }
    }

    private static void CollectMethodAnalysis(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol componentBaseSymbol,
        INamedTypeSymbol layoutComponentBaseSymbol,
        INamedTypeSymbol errorBoundarySymbol,
        INamedTypeSymbol renderModeAttributeSymbol,
        ConcurrentDictionary<INamedTypeSymbol, byte> allComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> localBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> localRelevantBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> boundaryWithErrorContentComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> boundaryComponentsWithBuiltInErrorContent,
        ConcurrentDictionary<INamedTypeSymbol, INamedTypeSymbol> rootBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> rootBoundaryKeyedComponents,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners,
        ConcurrentDictionary<IMethodSymbol, byte> buildRenderTreeRootMethods,
        ConcurrentDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses,
        ConcurrentDictionary<string, byte> boundaryComponentNames,
        ConcurrentDictionary<INamedTypeSymbol, ComponentDiagnosticLocations> componentDiagnosticLocations,
        ConcurrentDictionary<INamedTypeSymbol, byte> locallyReportedMissingErrorBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> locallyReportedBoundaryMissingErrorContentComponents,
        ImmutableDictionary<string, AdditionalText> razorAdditionalFiles,
        ConcurrentDictionary<string, RazorMarkupAnalysis?> razorMarkupCache)
    {
        if (context.Node is not MethodDeclarationSyntax methodDeclaration ||
            TryGetDeclaredMethodSymbol(methodDeclaration, context.SemanticModel, context.CancellationToken) is not IMethodSymbol declaredMethod)
        {
            return;
        }

        var methodSymbol = NormalizeMethodSymbol(declaredMethod);
        var containingType = methodSymbol.ContainingType;
        if (!IsComponent(containingType, componentBaseSymbol))
        {
            return;
        }

        if (methodDeclaration.Body is not null && IsRenderMethod(methodSymbol))
        {
            foreach (var childComponent in GetRenderedChildComponents(methodDeclaration.Body, context.SemanticModel, componentBaseSymbol, context.CancellationToken))
            {
                allComponents.TryAdd(childComponent, 0);
                var owners = componentOwners.GetOrAdd(childComponent, static _ => new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default));
                owners.TryAdd(containingType, 0);
            }
        }

        if (methodSymbol.Name == "BuildRenderTree")
        {
            if (methodDeclaration.Body is null)
            {
                return;
            }

            var renderTreeAnalysis = AnalyzeBuildRenderTree(methodDeclaration.Body, context.SemanticModel, componentBaseSymbol, errorBoundarySymbol, context.CancellationToken);
            if (renderTreeAnalysis.RootBoundaryComponent is not null)
            {
                rootBoundaryComponents[containingType] = renderTreeAnalysis.RootBoundaryComponent;
            }

            var razorMarkupAnalysis = TryGetRazorMarkupAnalysis(
                methodDeclaration,
                methodSymbol,
                boundaryComponentNames.Keys.ToImmutableHashSet(StringComparer.Ordinal),
                razorAdditionalFiles,
                razorMarkupCache,
                context.CancellationToken);
            var combinedRootAnalysis = CombineRootAnalysis(
                renderTreeAnalysis,
                razorMarkupAnalysis,
                containingType.GetPreferredSourceLocation());
            var hasBoundaryRoot = combinedRootAnalysis.HasBoundaryRoot;
            var boundaryRootHasErrorContent = combinedRootAnalysis.BoundaryRootHasErrorContent;
            var rootBoundaryIsKeyed = combinedRootAnalysis.RootBoundaryIsKeyed;
            var rootBoundaryUsesStaleRouteKey = combinedRootAnalysis.RootBoundaryUsesStaleRouteKey;
            var hasUnprotectedRoot = combinedRootAnalysis.FirstUnprotectedRootLocation is not null;
            var hasRelevantChildren = renderTreeAnalysis.ChildComponents.Count > 0;
            var missingBoundaryLocation = combinedRootAnalysis.FirstUnprotectedRootLocation ??
                containingType.GetPreferredSourceLocation();
            var boundaryLocation = combinedRootAnalysis.BoundaryRootLocation ??
                containingType.GetPreferredSourceLocation();

            componentDiagnosticLocations[containingType] = new ComponentDiagnosticLocations(
                containingType.PreferNonGeneratedSourceLocation(missingBoundaryLocation),
                containingType.PreferNonGeneratedSourceLocation(boundaryLocation));

            if (hasBoundaryRoot || hasUnprotectedRoot || hasRelevantChildren)
            {
                localRelevantBoundaryComponents.TryAdd(containingType, 0);
            }

            if (containingType.InheritsFromOrEquals(errorBoundarySymbol) &&
                BoundaryTypeHasBuiltInErrorContent(containingType, errorBoundarySymbol, context.CancellationToken))
            {
                localBoundaryComponents.TryAdd(containingType, 0);
                boundaryComponentsWithBuiltInErrorContent.TryAdd(containingType, 0);
                boundaryWithErrorContentComponents.TryAdd(containingType, 0);
            }

            if (hasBoundaryRoot)
            {
                localBoundaryComponents.TryAdd(containingType, 0);
                if (rootBoundaryIsKeyed)
                {
                    rootBoundaryKeyedComponents.TryAdd(containingType, 0);
                }

                if (boundaryRootHasErrorContent)
                {
                    boundaryWithErrorContentComponents.TryAdd(containingType, 0);
                }
            }

            if (containingType.InheritsFromOrEquals(layoutComponentBaseSymbol) &&
                hasBoundaryRoot &&
                !rootBoundaryIsKeyed &&
                boundaryLocation is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.LayoutBoundaryShouldBeRouteKeyed,
                    containingType.PreferNonGeneratedSourceLocation(boundaryLocation) ?? boundaryLocation,
                    containingType.Name));
            }

            if (containingType.InheritsFromOrEquals(layoutComponentBaseSymbol) &&
                hasBoundaryRoot &&
                rootBoundaryUsesStaleRouteKey &&
                boundaryLocation is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.LayoutBoundaryUsesStaleRouteKey,
                    containingType.PreferNonGeneratedSourceLocation(boundaryLocation) ?? boundaryLocation,
                    containingType.Name));
            }

            foreach (var referencedMethod in GetBuildRenderTreeReferencedMethods(methodDeclaration, context.SemanticModel, containingType, context.CancellationToken))
            {
                buildRenderTreeRootMethods.TryAdd(referencedMethod, 0);
            }

            foreach (var childComponent in renderTreeAnalysis.ChildComponents)
            {
                allComponents.TryAdd(childComponent, 0);
                var owners = componentOwners.GetOrAdd(childComponent, static _ => new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default));
                owners.TryAdd(containingType, 0);
            }

            foreach (var referencedComponent in GetReferencedComponentBuildRenderTrees(methodDeclaration, context.SemanticModel, containingType, componentBaseSymbol, context.CancellationToken))
            {
                allComponents.TryAdd(referencedComponent, 0);
                var owners = componentOwners.GetOrAdd(referencedComponent, static _ => new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default));
                owners.TryAdd(containingType, 0);
            }

            return;
        }

        if (GetDeclaredRenderModeKey(containingType, renderModeAttributeSymbol) is null ||
            !IsRelevantMethod(methodSymbol, containingType))
        {
            return;
        }

        var analysis = AnalyzeMethod(methodDeclaration, methodSymbol, context.SemanticModel, context.CancellationToken);
        methodAnalyses[methodSymbol] = analysis;

        var methodLocation = methodSymbol.GetPreferredSourceLocation();
        if (methodLocation is null)
        {
            return;
        }

        if (analysis.IsLifecycleMethod && analysis.HasOperationalCode && !analysis.HasTryCatch)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.LifecycleMissingTryCatch,
                methodLocation,
                methodSymbol.Name,
                containingType.Name));
        }

        if (analysis.IsDisposeMethod && analysis.HasOperationalCode && !analysis.HasTryCatch)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.DisposeMissingTryCatch,
                methodLocation,
                methodSymbol.Name,
                containingType.Name));
        }

        if (analysis.HasJsInteropCalls && !analysis.HasTryCatch)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.JsInteropMissingTryCatch,
                methodLocation,
                methodSymbol.Name,
                containingType.Name));
        }

        if (analysis.HasUnsafeLifecycleJsInterop)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.JsInteropRequiresInteractivityGuard,
                methodLocation,
                methodSymbol.Name,
                containingType.Name));
        }

        if (analysis.IsAsyncVoid)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.AsyncVoidMethod,
                methodLocation,
                methodSymbol.Name,
                containingType.Name));
        }

        foreach (var catchLocation in analysis.CatchWithoutLoggingLocations)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.CatchWithoutLogging,
                catchLocation,
                methodSymbol.Name,
                containingType.Name));
        }
    }

    private static void CollectLocalMissingTryCatchAnalysis(
        SyntaxNodeAnalysisContext context,
        INamedTypeSymbol componentBaseSymbol,
        INamedTypeSymbol componentSymbol,
        ConcurrentDictionary<IMethodSymbol, byte> localBuildRenderTreeRootMethods,
        ConcurrentDictionary<IMethodSymbol, MethodAnalysis> localMethodAnalyses)
    {
        if (context.Node is not MethodDeclarationSyntax methodDeclaration ||
            TryGetDeclaredMethodSymbol(methodDeclaration, context.SemanticModel, context.CancellationToken) is not IMethodSymbol declaredMethod)
        {
            return;
        }

        var methodSymbol = NormalizeMethodSymbol(declaredMethod);
        if (!SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType, componentSymbol) ||
            !IsComponent(componentSymbol, componentBaseSymbol))
        {
            return;
        }

        if (methodSymbol.Name == "BuildRenderTree")
        {
            foreach (var referencedMethod in GetBuildRenderTreeReferencedMethods(methodDeclaration, context.SemanticModel, componentSymbol, context.CancellationToken))
            {
                localBuildRenderTreeRootMethods.TryAdd(referencedMethod, 0);
            }

            return;
        }

        if (!IsRelevantMethod(methodSymbol, componentSymbol))
        {
            return;
        }

        localMethodAnalyses[methodSymbol] = AnalyzeMethod(methodDeclaration, methodSymbol, context.SemanticModel, context.CancellationToken);
    }

    private static void ReportLocalMissingTryCatchDiagnostics(
        SymbolAnalysisContext context,
        INamedTypeSymbol componentSymbol,
        ConcurrentDictionary<IMethodSymbol, byte> localBuildRenderTreeRootMethods,
        ConcurrentDictionary<IMethodSymbol, MethodAnalysis> localMethodAnalyses,
        ConcurrentDictionary<IMethodSymbol, byte> locallyReportedMissingTryCatchMethods,
        BoundaryCoverageResolver boundaryCoverageResolver)
    {
        if (localMethodAnalyses.Count == 0 ||
            !boundaryCoverageResolver.IsRelevantComponent(componentSymbol, context.CancellationToken) ||
            boundaryCoverageResolver.IsBoundaryProtected(componentSymbol, context.CancellationToken))
        {
            return;
        }

        var methods = localMethodAnalyses.Keys
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
        var rootMethods = methods
            .Where(method => localMethodAnalyses[method].IsApiRootCandidate || localBuildRenderTreeRootMethods.ContainsKey(method))
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
        var methodsWithSpecificTryCatchDiagnostics = methods
            .Where(method => HasSpecificTryCatchDiagnostic(localMethodAnalyses[method]))
            .ToImmutableHashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var unsafeMethods = FindUnsafeReachableMethods(rootMethods, localMethodAnalyses);

                foreach (var method in unsafeMethods.OrderBy(static method => method.GetPreferredSourceLocation().TryGetStartLine() ?? int.MaxValue))
        {
            if (methodsWithSpecificTryCatchDiagnostics.Contains(method) || !locallyReportedMissingTryCatchMethods.TryAdd(method, 0))
            {
                continue;
            }

            var methodLocation = method.GetPreferredSourceLocation();
            if (methodLocation is null)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MissingTryCatch,
                methodLocation,
                method.Name,
                componentSymbol.Name));
        }
    }

    private static void AnalyzeCompilationEnd(
        CompilationAnalysisContext context,
        ConcurrentDictionary<INamedTypeSymbol, byte> allComponents,
        ConcurrentDictionary<INamedTypeSymbol, string?> declaredRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, byte> interactiveComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> localBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> localRelevantBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> boundaryWithErrorContentComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> boundaryComponentsWithBuiltInErrorContent,
        ConcurrentDictionary<INamedTypeSymbol, INamedTypeSymbol> rootBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> rootBoundaryKeyedComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> layoutComponents,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners,
        ConcurrentDictionary<IMethodSymbol, byte> buildRenderTreeRootMethods,
        ConcurrentDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses,
        ConcurrentDictionary<INamedTypeSymbol, ComponentDiagnosticLocations> componentDiagnosticLocations,
        ConcurrentDictionary<INamedTypeSymbol, byte> locallyReportedMissingErrorBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> locallyReportedBoundaryMissingErrorContentComponents,
        ConcurrentDictionary<IMethodSymbol, byte> locallyReportedMissingTryCatchMethods)
    {
        var effectiveRenderModes = ComputeEffectiveRenderModes(allComponents.Keys, declaredRenderModes, componentOwners);
        var relevantComponents = ComputeRelevantBoundaryComponents(effectiveRenderModes, componentOwners, localRelevantBoundaryComponents);
        var boundaryProtectedRenderModes = ComputeBoundaryProtectedRenderModes(relevantComponents, effectiveRenderModes, localBoundaryComponents, componentOwners);
        var boundaryProtectedComponents = ComputeBoundaryProtectedComponents(relevantComponents, effectiveRenderModes, boundaryProtectedRenderModes);
        var suggestedBoundaryResolvers = ComputeSuggestedBoundaryResolvers(relevantComponents, effectiveRenderModes, boundaryProtectedRenderModes, componentOwners);

        foreach (var component in relevantComponents.OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            var boundaryProtected = boundaryProtectedComponents.Contains(component);
            var diagnosticLocations = componentDiagnosticLocations.TryGetValue(component, out var knownLocations)
                ? knownLocations
                : new ComponentDiagnosticLocations(component.GetPreferredSourceLocation(), component.GetPreferredSourceLocation());
            var missingBoundaryLocation = component.PreferNonGeneratedSourceLocation(diagnosticLocations.MissingErrorBoundaryLocation);
            var boundaryLocation = component.PreferNonGeneratedSourceLocation(diagnosticLocations.BoundaryLocation);

            if (!boundaryProtected &&
                ShouldReportMissingErrorBoundary(component, declaredRenderModes, effectiveRenderModes, boundaryProtectedRenderModes, componentOwners) &&
                !locallyReportedMissingErrorBoundaryComponents.ContainsKey(component) &&
                missingBoundaryLocation is not null)
            {
                var resolverSymbols = suggestedBoundaryResolvers.TryGetValue(component, out var resolvers) && resolvers.Length > 0
                    ? resolvers
                    : [component];

                foreach (var resolverSymbol in resolverSymbols)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.MissingErrorBoundary,
                        missingBoundaryLocation,
                        component.Name,
                        resolverSymbol.Name,
                        resolverSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty)));
                }
            }

            var boundaryHasErrorContent =
                boundaryWithErrorContentComponents.ContainsKey(component) ||
                (rootBoundaryComponents.TryGetValue(component, out var rootBoundaryComponent) &&
                 boundaryComponentsWithBuiltInErrorContent.ContainsKey(rootBoundaryComponent));

            if (localBoundaryComponents.ContainsKey(component) &&
                !boundaryHasErrorContent &&
                !locallyReportedBoundaryMissingErrorContentComponents.ContainsKey(component) &&
                boundaryLocation is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.ErrorBoundaryMissingErrorContent,
                    boundaryLocation,
                    component.Name));
            }

            var methods = component
                .GetMembers()
                .OfType<IMethodSymbol>()
                .Select(NormalizeMethodSymbol)
                .Where(method => methodAnalyses.ContainsKey(method))
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                .ToArray();

            var rootMethods = methods
                .Where(method => methodAnalyses[method].IsApiRootCandidate || buildRenderTreeRootMethods.ContainsKey(method))
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                .ToArray();

            var methodsWithSpecificTryCatchDiagnostics = methods
                .Where(method => HasSpecificTryCatchDiagnostic(methodAnalyses[method]))
                .ToImmutableHashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

            if (!boundaryProtected)
            {
                var unsafeMethods = FindUnsafeReachableMethods(rootMethods, methodAnalyses);
                foreach (var method in unsafeMethods.OrderBy(static method => method.Locations.FirstOrDefault(static location => location.IsInSource).TryGetStartLine() ?? int.MaxValue))
                {
                    if (methodsWithSpecificTryCatchDiagnostics.Contains(method) ||
                        locallyReportedMissingTryCatchMethods.ContainsKey(method))
                    {
                        continue;
                    }

                    var methodLocation = method.GetPreferredSourceLocation();
                    if (methodLocation is null)
                    {
                        continue;
                    }

                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.MissingTryCatch,
                        methodLocation,
                        method.Name,
                        component.Name));
                }
            }
        }

    }

    private static Dictionary<INamedTypeSymbol, ImmutableHashSet<string>> ComputeEffectiveRenderModes(
        IEnumerable<INamedTypeSymbol> allComponents,
        ConcurrentDictionary<INamedTypeSymbol, string?> declaredRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners)
    {
        var allComponentSet = new HashSet<INamedTypeSymbol>(allComponents, SymbolEqualityComparer.Default);
        var effectiveRenderModes = new Dictionary<INamedTypeSymbol, ImmutableHashSet<string>.Builder>(SymbolEqualityComparer.Default);
        foreach (var component in allComponentSet)
        {
            effectiveRenderModes[component] = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        }

        foreach (var component in allComponentSet)
        {
            if (declaredRenderModes.TryGetValue(component, out var declaredRenderMode) &&
                declaredRenderMode is not null)
            {
                effectiveRenderModes[component].Add(declaredRenderMode);
            }
        }

        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var component in allComponentSet)
            {
                if (declaredRenderModes.TryGetValue(component, out var declaredRenderMode) &&
                    declaredRenderMode is not null)
                {
                    continue;
                }

                if (!componentOwners.TryGetValue(component, out var owners) || owners.IsEmpty)
                {
                    changed |= effectiveRenderModes[component].Add(StaticRenderModeKey);
                    continue;
                }

                foreach (var owner in owners.Keys)
                {
                    foreach (var renderMode in effectiveRenderModes[owner])
                    {
                        changed |= effectiveRenderModes[component].Add(renderMode);
                    }
                }
            }
        }

        var results = new Dictionary<INamedTypeSymbol, ImmutableHashSet<string>>(SymbolEqualityComparer.Default);
        foreach (var pair in effectiveRenderModes)
        {
            results[pair.Key] = pair.Value.ToImmutable();
        }

        return results;
    }

    private static HashSet<INamedTypeSymbol> ComputeRelevantBoundaryComponents(
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners,
        ConcurrentDictionary<INamedTypeSymbol, byte> localRelevantBoundaryComponents)
    {
        var relevantComponents = new HashSet<INamedTypeSymbol>(
            localRelevantBoundaryComponents.Keys
                .Where(component => effectiveRenderModes.TryGetValue(component, out var renderModes) &&
                    renderModes.Any(static renderMode => !string.Equals(renderMode, StaticRenderModeKey, StringComparison.Ordinal))),
            SymbolEqualityComparer.Default);

        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var component in relevantComponents.ToArray())
            {
                if (!componentOwners.TryGetValue(component, out var owners))
                {
                    continue;
                }

                foreach (var owner in owners.Keys)
                {
                    changed |= relevantComponents.Add(owner);
                }
            }

            foreach (var pair in componentOwners)
            {
                var child = pair.Key;
                if (!effectiveRenderModes.TryGetValue(child, out var childRenderModes) ||
                    !childRenderModes.Any(static renderMode => !string.Equals(renderMode, StaticRenderModeKey, StringComparison.Ordinal)))
                {
                    continue;
                }

                foreach (var owner in pair.Value.Keys)
                {
                    if (!relevantComponents.Contains(owner))
                    {
                        continue;
                    }

                    if (!localRelevantBoundaryComponents.ContainsKey(child))
                    {
                        continue;
                    }

                    changed |= relevantComponents.Add(child);
                    break;
                }
            }
        }

        return relevantComponents;
    }

    private static Dictionary<INamedTypeSymbol, ImmutableHashSet<string>> ComputeBoundaryProtectedRenderModes(
        IEnumerable<INamedTypeSymbol> relevantComponents,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, byte> localBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners)
    {
        var relevantComponentSet = new HashSet<INamedTypeSymbol>(relevantComponents, SymbolEqualityComparer.Default);
        var allComponentSet = new HashSet<INamedTypeSymbol>(effectiveRenderModes.Keys, SymbolEqualityComparer.Default);
        var protectedRenderModes = new Dictionary<INamedTypeSymbol, ImmutableHashSet<string>.Builder>(SymbolEqualityComparer.Default);
        foreach (var component in allComponentSet)
        {
            protectedRenderModes[component] = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        }

        foreach (var component in allComponentSet)
        {
            if (localBoundaryComponents.ContainsKey(component))
            {
                protectedRenderModes[component].UnionWith(effectiveRenderModes[component]);
                continue;
            }

            for (var currentBaseType = component.BaseType; currentBaseType is not null; currentBaseType = currentBaseType.BaseType)
            {
                var inheritedBoundaryComponent =
                    relevantComponentSet.Contains(currentBaseType) && localBoundaryComponents.ContainsKey(currentBaseType)
                        ? currentBaseType
                        : relevantComponentSet.Contains(currentBaseType.OriginalDefinition) && localBoundaryComponents.ContainsKey(currentBaseType.OriginalDefinition)
                            ? currentBaseType.OriginalDefinition
                            : null;

                if (inheritedBoundaryComponent is null)
                {
                    continue;
                }

                protectedRenderModes[component].UnionWith(effectiveRenderModes[component]);
                break;
            }
        }

        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var component in allComponentSet)
            {
                if (!componentOwners.TryGetValue(component, out var owners) || owners.IsEmpty)
                {
                    continue;
                }

                foreach (var renderMode in effectiveRenderModes[component])
                {
                    if (protectedRenderModes[component].Contains(renderMode))
                    {
                        continue;
                    }

                    var actionableOwners = GetConcreteComponents(
                            owners.Keys,
                            allComponentSet)
                        .ToArray();

                    if (actionableOwners.Length == 0)
                    {
                        continue;
                    }

                    var allOwnersCovered = true;
                    foreach (var owner in actionableOwners)
                    {
                        if (!effectiveRenderModes.TryGetValue(owner, out var ownerRenderModes) ||
                            !ownerRenderModes.Contains(renderMode) ||
                            !protectedRenderModes[owner].Contains(renderMode))
                        {
                            allOwnersCovered = false;
                            break;
                        }
                    }

                    if (allOwnersCovered)
                    {
                        changed |= protectedRenderModes[component].Add(renderMode);
                    }
                }
            }
        }

        var result = new Dictionary<INamedTypeSymbol, ImmutableHashSet<string>>(SymbolEqualityComparer.Default);
        foreach (var pair in protectedRenderModes)
        {
            result[pair.Key] = pair.Value.ToImmutable();
        }

        return result;
    }

    private static HashSet<INamedTypeSymbol> ComputeBoundaryProtectedComponents(
        IEnumerable<INamedTypeSymbol> relevantComponents,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> protectedRenderModes)
    {
        var relevantComponentSet = new HashSet<INamedTypeSymbol>(relevantComponents, SymbolEqualityComparer.Default);
        return new HashSet<INamedTypeSymbol>(
            relevantComponentSet.Where(component => effectiveRenderModes[component].All(renderMode => protectedRenderModes[component].Contains(renderMode))),
            SymbolEqualityComparer.Default);
    }

    private static Dictionary<INamedTypeSymbol, ImmutableArray<INamedTypeSymbol>> ComputeSuggestedBoundaryResolvers(
        IEnumerable<INamedTypeSymbol> relevantComponents,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> protectedRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners)
    {
        var relevantComponentSet = new HashSet<INamedTypeSymbol>(relevantComponents, SymbolEqualityComparer.Default);
        var allComponentSet = new HashSet<INamedTypeSymbol>(effectiveRenderModes.Keys, SymbolEqualityComparer.Default);
        var resolverByRenderMode = new Dictionary<INamedTypeSymbol, Dictionary<string, HashSet<INamedTypeSymbol>>>(SymbolEqualityComparer.Default);
        foreach (var component in relevantComponentSet)
        {
            var renderModeResolvers = new Dictionary<string, HashSet<INamedTypeSymbol>>(StringComparer.Ordinal);
            foreach (var renderMode in effectiveRenderModes[component])
            {
                renderModeResolvers[renderMode] = [component];
            }

            resolverByRenderMode[component] = renderModeResolvers;
        }

        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var component in relevantComponentSet)
            {
                foreach (var renderMode in effectiveRenderModes[component])
                {
                    var ownersWithSameRenderMode = componentOwners.TryGetValue(component, out var owners)
                        ? GetConcreteComponentsForRenderMode(
                                owners.Keys,
                                renderMode,
                                allComponentSet,
                                effectiveRenderModes)
                            .Where(owner =>
                                !protectedRenderModes.TryGetValue(owner, out var ownerProtectedRenderModes) ||
                                !ownerProtectedRenderModes.Contains(renderMode))
                            .ToArray()
                        : [];

                    var resolvers = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
                    if (ownersWithSameRenderMode.Length > 0)
                    {
                        foreach (var owner in ownersWithSameRenderMode)
                        {
                            if (resolverByRenderMode.TryGetValue(owner, out var ownerResolversByRenderMode) &&
                                ownerResolversByRenderMode.TryGetValue(renderMode, out var ownerResolvers))
                            {
                                resolvers.UnionWith(ownerResolvers);
                            }
                            else
                            {
                                resolvers.Add(owner);
                            }
                        }
                    }
                    else
                    {
                        resolvers.Add(component);
                    }

                    if (!resolverByRenderMode[component][renderMode].SetEquals(resolvers))
                    {
                        resolverByRenderMode[component][renderMode] = resolvers;
                        changed = true;
                    }
                }
            }
        }

        var suggestedResolvers = new Dictionary<INamedTypeSymbol, ImmutableArray<INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        foreach (var component in relevantComponentSet)
        {
            var distinctResolvers = resolverByRenderMode[component]
                .SelectMany(pair => ExpandConcreteResolverSymbols(
                    pair.Value,
                    pair.Key,
                    allComponentSet,
                    effectiveRenderModes,
                    resolverByRenderMode))
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                .OrderBy(static resolver => resolver.ToDisplayString(), StringComparer.Ordinal)
                .ToImmutableArray();

            suggestedResolvers[component] = distinctResolvers;
        }

        return suggestedResolvers;
    }

    private static IEnumerable<INamedTypeSymbol> ExpandConcreteResolverSymbols(
        IEnumerable<INamedTypeSymbol> resolvers,
        string renderMode,
        IReadOnlyCollection<INamedTypeSymbol> relevantComponents,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes,
        IReadOnlyDictionary<INamedTypeSymbol, Dictionary<string, HashSet<INamedTypeSymbol>>> resolverByRenderMode)
    {
        var expandedResolvers = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var pendingResolvers = new Stack<INamedTypeSymbol>(resolvers.Reverse());
        var visitedResolvers = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        while (pendingResolvers.Count > 0)
        {
            var resolver = pendingResolvers.Pop();
            if (!visitedResolvers.Add(resolver))
            {
                continue;
            }

            if (!resolver.IsAbstract)
            {
                expandedResolvers.Add(resolver);
                continue;
            }

            var concreteDescendants = relevantComponents
                .Where(candidate =>
                    !candidate.IsAbstract &&
                    !SymbolEqualityComparer.Default.Equals(candidate, resolver) &&
                    candidate.InheritsFromOrEquals(resolver) &&
                    effectiveRenderModes.TryGetValue(candidate, out var candidateRenderModes) &&
                    candidateRenderModes.Contains(renderMode))
                .ToArray();

            if (concreteDescendants.Length == 0)
            {
                continue;
            }

            foreach (var concreteDescendant in concreteDescendants)
            {
                if (resolverByRenderMode.TryGetValue(concreteDescendant, out var descendantResolversByRenderMode) &&
                    descendantResolversByRenderMode.TryGetValue(renderMode, out var descendantResolvers) &&
                    descendantResolvers.Count > 0)
                {
                    foreach (var descendantResolver in descendantResolvers)
                    {
                        pendingResolvers.Push(descendantResolver);
                    }
                }
                else
                {
                    pendingResolvers.Push(concreteDescendant);
                }
            }
        }

        return expandedResolvers;
    }

    private static IEnumerable<INamedTypeSymbol> GetConcreteComponentsForRenderMode(
        IEnumerable<INamedTypeSymbol> components,
        string renderMode,
        IReadOnlyCollection<INamedTypeSymbol> allComponents,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes)
    {
        var expandedComponents = GetConcreteComponents(components, allComponents);
        return expandedComponents.Where(component =>
            effectiveRenderModes.TryGetValue(component, out var componentRenderModes) &&
            componentRenderModes.Contains(renderMode));
    }

    private static IEnumerable<INamedTypeSymbol> GetConcreteComponents(
        IEnumerable<INamedTypeSymbol> components,
        IReadOnlyCollection<INamedTypeSymbol> allComponents)
    {
        var expandedComponents = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var component in components)
        {
            foreach (var expandedComponent in ExpandConcreteComponentSymbols(component, allComponents))
            {
                expandedComponents.Add(expandedComponent);
            }
        }

        return expandedComponents;
    }

    private static IEnumerable<INamedTypeSymbol> ExpandConcreteComponentSymbols(
        INamedTypeSymbol component,
        IReadOnlyCollection<INamedTypeSymbol> allComponents)
    {
        if (!component.IsAbstract)
        {
            return [component];
        }

        return allComponents.Where(candidate =>
            !candidate.IsAbstract &&
            !SymbolEqualityComparer.Default.Equals(candidate, component) &&
            candidate.InheritsFromOrEquals(component));
    }

    private static bool ShouldReportMissingErrorBoundary(
        INamedTypeSymbol component,
        IReadOnlyDictionary<INamedTypeSymbol, string?> declaredRenderModes,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> protectedRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners)
    {
        if (component.IsAbstract)
        {
            return false;
        }
        
        return true;
    }

    private static bool IsComponent(INamedTypeSymbol namedType, INamedTypeSymbol componentBaseSymbol) =>
        namedType.TypeKind == TypeKind.Class &&
        namedType.InheritsFromOrEquals(componentBaseSymbol) &&
        namedType.HasPathContaining(".razor");

    private static string? GetDeclaredRenderModeKey(INamedTypeSymbol namedType, INamedTypeSymbol renderModeAttributeSymbol)
    {
        foreach (var attribute in namedType.GetAttributes())
        {
            if (attribute.AttributeClass is INamedTypeSymbol attributeClass &&
                attributeClass.InheritsFromOrEquals(renderModeAttributeSymbol))
            {
                return GetRenderModeKey(attributeClass);
            }
        }

        return null;
    }

    private static string GetRenderModeKey(INamedTypeSymbol renderModeAttribute)
    {
        var modeProperty = renderModeAttribute.GetMembers("Mode").OfType<IPropertySymbol>().FirstOrDefault();
        if (modeProperty?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is PropertyDeclarationSyntax propertyDeclaration)
        {
            if (propertyDeclaration.ExpressionBody is { Expression: { } expression })
            {
                return NormalizeRenderModeKey(expression.ToString());
            }

            if (propertyDeclaration.AccessorList?.Accessors.FirstOrDefault(static accessor => accessor.Keyword.IsKind(SyntaxKind.GetKeyword)) is { } getter)
            {
                if (getter.ExpressionBody is { Expression: { } getterExpression })
                {
                    return NormalizeRenderModeKey(getterExpression.ToString());
                }

                if (getter.Body?.Statements.OfType<ReturnStatementSyntax>().FirstOrDefault() is { Expression: { } returnExpression })
                {
                    return NormalizeRenderModeKey(returnExpression.ToString());
                }
            }
        }

        return renderModeAttribute.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
    }

    private static string NormalizeRenderModeKey(string renderModeExpression) =>
        string.Concat(renderModeExpression.Where(static character => !char.IsWhiteSpace(character)));

    private static ImmutableDictionary<string, AdditionalText> CreateRazorAdditionalFileMap(ImmutableArray<AdditionalText> additionalFiles)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, AdditionalText>(StringComparer.OrdinalIgnoreCase);
        foreach (var additionalFile in additionalFiles)
        {
            if (additionalFile.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            {
                builder[additionalFile.Path] = additionalFile;
            }
        }

        return builder.ToImmutable();
    }

    private static RazorMarkupAnalysis? TryGetRazorMarkupAnalysis(
        MethodDeclarationSyntax methodDeclaration,
        IMethodSymbol methodSymbol,
        ImmutableHashSet<string> boundaryComponentNames,
        ImmutableDictionary<string, AdditionalText> razorAdditionalFiles,
        ConcurrentDictionary<string, RazorMarkupAnalysis?> razorMarkupCache,
        CancellationToken cancellationToken)
    {
        var razorPath = TryGetRazorPath(methodDeclaration, methodSymbol);
        if (razorPath is null)
        {
            return null;
        }

        return razorMarkupCache.GetOrAdd(razorPath, path =>
            RazorMarkupAnalyzer.Analyze(
                path,
                candidatePath =>
                {
                    if (TryGetRazorAdditionalFileText(candidatePath, razorAdditionalFiles, cancellationToken) is { } sourceText)
                    {
                        return sourceText;
                    }

                    return null;
                },
                boundaryComponentNames));
    }

    private static string? TryGetRazorPath(MethodDeclarationSyntax methodDeclaration, IMethodSymbol methodSymbol)
    {
        foreach (var statement in methodDeclaration.Body?.Statements ?? [])
        {
            var path = statement.GetLocation().TryGetSourcePath();
            if (path?.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) == true)
            {
                return path;
            }
        }

        var methodLocationPath = methodSymbol.GetPreferredSourceLocation().TryGetSourcePath();
        if (methodLocationPath?.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) == true)
        {
            return methodLocationPath;
        }

        return methodSymbol.TryGetRazorFilePath();
    }

    private static CombinedRootAnalysis CombineRootAnalysis(
        RenderTreeAnalysis generatedAnalysis,
        RazorMarkupAnalysis? razorAnalysis,
        Location? defaultLocation)
    {
        if (razorAnalysis is null)
        {
            return new CombinedRootAnalysis(
                generatedAnalysis.HasBoundaryRoot,
                generatedAnalysis.BoundaryRootHasErrorContent,
                generatedAnalysis.RootBoundaryIsKeyed,
                generatedAnalysis.RootBoundaryUsesStaleRouteKey,
                generatedAnalysis.FirstUnprotectedRootLocation,
                generatedAnalysis.BoundaryRootLocation ?? defaultLocation);
        }

        var generatedHasTrustedBoundaryRoot =
            generatedAnalysis.HasBoundaryRoot &&
            generatedAnalysis.FirstUnprotectedRootLocation is null;

        if (generatedHasTrustedBoundaryRoot)
        {
            return new CombinedRootAnalysis(
                hasBoundaryRoot: true,
                boundaryRootHasErrorContent: generatedAnalysis.BoundaryRootHasErrorContent,
                rootBoundaryIsKeyed: generatedAnalysis.RootBoundaryIsKeyed,
                rootBoundaryUsesStaleRouteKey: generatedAnalysis.RootBoundaryUsesStaleRouteKey,
                firstUnprotectedRootLocation: null,
                boundaryRootLocation: razorAnalysis.BoundaryRootLocation ??
                    generatedAnalysis.BoundaryRootLocation ??
                    defaultLocation);
        }

        return new CombinedRootAnalysis(
            hasBoundaryRoot: generatedAnalysis.HasBoundaryRoot || razorAnalysis.HasBoundaryRoot,
            boundaryRootHasErrorContent: (!generatedAnalysis.HasBoundaryRoot || generatedAnalysis.BoundaryRootHasErrorContent) &&
                (!razorAnalysis.HasBoundaryRoot || razorAnalysis.BoundaryRootHasErrorContent),
            rootBoundaryIsKeyed: (!generatedAnalysis.HasBoundaryRoot || generatedAnalysis.RootBoundaryIsKeyed) &&
                (!razorAnalysis.HasBoundaryRoot || razorAnalysis.BoundaryRootIsKeyed),
            rootBoundaryUsesStaleRouteKey: generatedAnalysis.RootBoundaryUsesStaleRouteKey,
            firstUnprotectedRootLocation: razorAnalysis.FirstUnprotectedRootLocation ??
                generatedAnalysis.FirstUnprotectedRootLocation,
            boundaryRootLocation: razorAnalysis.BoundaryRootLocation ??
                generatedAnalysis.BoundaryRootLocation ??
                defaultLocation);
    }

    private static SourceText? TryGetRazorAdditionalFileText(
        string candidatePath,
        ImmutableDictionary<string, AdditionalText> razorAdditionalFiles,
        CancellationToken cancellationToken)
    {
        if (razorAdditionalFiles.TryGetValue(candidatePath, out var additionalText))
        {
            return additionalText.GetText(cancellationToken);
        }

        var normalizedCandidatePath = candidatePath.Replace('/', '\\').TrimStart('\\');
        foreach (var pair in razorAdditionalFiles)
        {
            var normalizedAdditionalPath = pair.Key.Replace('/', '\\');
            if (normalizedAdditionalPath.EndsWith(normalizedCandidatePath, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value.GetText(cancellationToken);
            }
        }

        return null;
    }

    private static bool IsRenderMethod(IMethodSymbol methodSymbol)
    {
        if (methodSymbol.IsImplicitlyDeclared || !methodSymbol.ReturnsVoid)
        {
            return false;
        }

        if (methodSymbol.Name == "BuildRenderTree")
        {
            return true;
        }

        return methodSymbol.Parameters.Any(parameter =>
            parameter.Type is INamedTypeSymbol parameterType &&
            GetTypeMetadataNames(parameterType).Any(static metadataName =>
                metadataName == "Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder"));
    }

    private static bool IsRelevantMethod(IMethodSymbol methodSymbol, INamedTypeSymbol containingType) =>
        methodSymbol.MethodKind == MethodKind.Ordinary &&
        !methodSymbol.IsImplicitlyDeclared &&
        methodSymbol.Name != "BuildRenderTree" &&
        SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType, containingType) &&
        methodSymbol.Locations.Any(static location => location.IsInSource);

    private static MethodAnalysis AnalyzeMethod(
        MethodDeclarationSyntax methodDeclaration,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var callees = ImmutableHashSet.CreateBuilder<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var callee in GetCalledMemberMethods(methodDeclaration, semanticModel, methodSymbol.ContainingType, cancellationToken))
        {
            callees.Add(callee);
        }

        var jsInteropCalls = GetJsInteropInvocations(methodDeclaration, semanticModel, cancellationToken).ToArray();

        return new MethodAnalysis(
            hasTryCatch: MethodContainsTryCatch(methodDeclaration),
            callees: callees.ToImmutable(),
            delegatedMethod: GetDelegatedMethod(methodDeclaration, semanticModel, methodSymbol.ContainingType, cancellationToken),
            isApiRootCandidate: IsApiRootCandidate(methodSymbol),
            isLifecycleMethod: IsLifecycleMethod(methodSymbol),
            isDisposeMethod: IsDisposeMethod(methodSymbol),
            hasOperationalCode: HasOperationalCode(methodDeclaration),
            hasJsInteropCalls: jsInteropCalls.Length > 0,
            hasUnsafeLifecycleJsInterop: IsPreRenderLifecycleMethod(methodSymbol) && jsInteropCalls.Any(call => !IsWithinInteractivityGuard(call)),
            isAsyncVoid: IsAsyncVoid(methodDeclaration, methodSymbol),
            catchWithoutLoggingLocations: GetCatchWithoutLoggingLocations(methodDeclaration, semanticModel, cancellationToken));
    }

    private static bool IsApiRootCandidate(IMethodSymbol methodSymbol) =>
        methodSymbol.DeclaredAccessibility != Accessibility.Private ||
        methodSymbol.IsOverride ||
        methodSymbol.ExplicitInterfaceImplementations.Length > 0;

    private static bool IsLifecycleMethod(IMethodSymbol methodSymbol) =>
        methodSymbol.Name is "OnInitialized" or "OnInitializedAsync" or "OnParametersSet" or "OnParametersSetAsync" or "OnAfterRender" or "OnAfterRenderAsync" or "SetParametersAsync";

    private static bool IsPreRenderLifecycleMethod(IMethodSymbol methodSymbol) =>
        methodSymbol.Name is "OnInitialized" or "OnInitializedAsync" or "OnParametersSet" or "OnParametersSetAsync" or "SetParametersAsync";

    private static bool IsDisposeMethod(IMethodSymbol methodSymbol) =>
        methodSymbol.Name is "Dispose" or "DisposeAsync";

    private static bool IsAsyncVoid(MethodDeclarationSyntax methodDeclaration, IMethodSymbol methodSymbol) =>
        methodSymbol.ReturnsVoid &&
        methodDeclaration.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.AsyncKeyword));

    private static bool HasSpecificTryCatchDiagnostic(MethodAnalysis analysis) =>
        !analysis.HasTryCatch &&
        ((analysis.IsLifecycleMethod && analysis.HasOperationalCode) ||
         (analysis.IsDisposeMethod && analysis.HasOperationalCode) ||
         analysis.HasJsInteropCalls);

    private static bool HasOperationalCode(MethodDeclarationSyntax methodDeclaration)
    {
        var rootNode = GetMethodExecutableRoot(methodDeclaration);
        if (rootNode is null)
        {
            return false;
        }

        return rootNode.DescendantNodesAndSelf(static node => !IsNestedFunctionLike(node)).Any(node =>
            node is InvocationExpressionSyntax or AwaitExpressionSyntax or ThrowStatementSyntax or ObjectCreationExpressionSyntax);
    }

    private static ImmutableHashSet<IMethodSymbol> FindUnsafeReachableMethods(
        IEnumerable<IMethodSymbol> rootMethods,
        IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        var unsafeMethods = ImmutableHashSet.CreateBuilder<IMethodSymbol>(SymbolEqualityComparer.Default);
        var visitedProtected = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var visitedUnprotected = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var effectiveSafetyCache = new Dictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);
        var visitingEffectiveSafety = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        foreach (var rootMethod in rootMethods)
        {
            Visit(rootMethod, callerProtected: false);
        }

        return unsafeMethods.ToImmutable();

        void Visit(IMethodSymbol method, bool callerProtected)
        {
            if (!methodAnalyses.TryGetValue(method, out var analysis))
            {
                return;
            }

            var visited = callerProtected ? visitedProtected : visitedUnprotected;
            if (!visited.Add(method))
            {
                return;
            }

            if (!callerProtected && !IsEffectivelySafe(method))
            {
                unsafeMethods.Add(method);
            }

            var descendantProtected = callerProtected || analysis.HasTryCatch;
            foreach (var callee in analysis.Callees)
            {
                Visit(callee, descendantProtected);
            }
        }

        bool IsEffectivelySafe(IMethodSymbol method)
        {
            if (effectiveSafetyCache.TryGetValue(method, out var cached))
            {
                return cached;
            }

            if (!methodAnalyses.TryGetValue(method, out var analysis))
            {
                return false;
            }

            if (analysis.HasTryCatch)
            {
                effectiveSafetyCache[method] = true;
                return true;
            }

            if (analysis.DelegatedMethod is null || !visitingEffectiveSafety.Add(method))
            {
                effectiveSafetyCache[method] = false;
                return false;
            }

            try
            {
                var safe = IsEffectivelySafe(analysis.DelegatedMethod);
                effectiveSafetyCache[method] = safe;
                return safe;
            }
            finally
            {
                visitingEffectiveSafety.Remove(method);
            }
        }
    }

    private static RenderTreeAnalysis AnalyzeBuildRenderTree(
        BlockSyntax body,
        SemanticModel semanticModel,
        INamedTypeSymbol componentBaseSymbol,
        INamedTypeSymbol errorBoundarySymbol,
        CancellationToken cancellationToken)
    {
        var childComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "OpenComponent" })
            {
                continue;
            }

            var childComponent = TryGetComponentType(invocation, semanticModel, cancellationToken);
            if (childComponent is not null &&
                childComponent.InheritsFromOrEquals(componentBaseSymbol) &&
                !IsIgnoredRootComponent(childComponent))
            {
                childComponents.Add(childComponent);
            }
        }

        return AnalyzeBuildRenderTreeStatements(
            body.Statements,
            childComponents.ToImmutable(),
            semanticModel,
            errorBoundarySymbol,
            cancellationToken);
    }

    private static RenderTreeAnalysis AnalyzeBuildRenderTreeStatements(
        IEnumerable<StatementSyntax> statements,
        ImmutableHashSet<INamedTypeSymbol> childComponents,
        SemanticModel semanticModel,
        INamedTypeSymbol errorBoundarySymbol,
        CancellationToken cancellationToken)
    {
        var hasUnprotectedInteractiveRoot = false;
        var hasBoundaryProtectedContent = false;
        var boundaryRootHasErrorContent = true;
        var rootBoundaryIsKeyed = true;
        var rootBoundaryUsesStaleRouteKey = false;
        INamedTypeSymbol? rootBoundaryComponent = null;
        Location? firstUnprotectedRootLocation = null;
        Location? boundaryRootLocation = null;
        RootAnalysisState? currentRoot = null;

        foreach (var statement in statements)
        {
            if (currentRoot is null && statement is IfStatementSyntax ifStatement)
            {
                MergeRenderTreeAnalysis(
                    AnalyzeStatementBranches(ifStatement, childComponents, semanticModel, errorBoundarySymbol, cancellationToken),
                    ref hasUnprotectedInteractiveRoot,
                    ref hasBoundaryProtectedContent,
                    ref boundaryRootHasErrorContent,
                    ref rootBoundaryIsKeyed,
                    ref rootBoundaryUsesStaleRouteKey,
                    ref rootBoundaryComponent,
                    ref firstUnprotectedRootLocation,
                    ref boundaryRootLocation);
                continue;
            }

            if (statement is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax invocation } ||
                invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var invocationName })
            {
                continue;
            }

            switch (invocationName)
            {
                case "OpenComponent":
                {
                    var componentType = TryGetComponentType(invocation, semanticModel, cancellationToken);
                    if (currentRoot is null)
                    {
                        currentRoot = RootAnalysisState.CreateComponentRoot(componentType, errorBoundarySymbol, cancellationToken, invocation.GetLocation());
                    }
                    else
                    {
                        currentRoot.OpenComponent(componentType, errorBoundarySymbol, invocation.GetLocation());
                    }

                    break;
                }

                case "OpenElement":
                    if (currentRoot is null)
                    {
                        currentRoot = RootAnalysisState.CreateElementRoot(invocation.GetLocation());
                    }
                    else
                    {
                        currentRoot.OpenElement(invocation.GetLocation());
                    }
                    break;

                case "AddAttribute":
                    currentRoot?.AnalyzeAttribute(invocation, semanticModel, cancellationToken);
                    break;

                case "SetKey":
                    currentRoot?.AnalyzeSetKey(invocation, semanticModel, cancellationToken);
                    break;

                case "CloseComponent":
                case "CloseElement":
                    if (currentRoot is null)
                    {
                        break;
                    }

                    currentRoot.CloseNode();
                    if (currentRoot.IsComplete)
                    {
                        hasUnprotectedInteractiveRoot |= currentRoot.HasUnprotectedInteractiveContent;
                        hasBoundaryProtectedContent |= currentRoot.HasBoundaryProtectedContent;
                        boundaryRootHasErrorContent &= !currentRoot.HasBoundaryMissingErrorContent;
                        rootBoundaryIsKeyed &= currentRoot.RootBoundaryIsKeyed;
                        rootBoundaryUsesStaleRouteKey |= currentRoot.RootBoundaryUsesStaleRouteKey;
                        rootBoundaryComponent ??= currentRoot.RootBoundaryComponent;
                        firstUnprotectedRootLocation ??= currentRoot.FirstUnprotectedContentLocation;
                        boundaryRootLocation ??= currentRoot.RootBoundaryLocation;
                        currentRoot = null;
                    }

                    break;
            }
        }

        if (currentRoot is not null)
        {
            MergeRootAnalysisState(
                currentRoot,
                ref hasUnprotectedInteractiveRoot,
                ref hasBoundaryProtectedContent,
                ref boundaryRootHasErrorContent,
                ref rootBoundaryIsKeyed,
                ref rootBoundaryUsesStaleRouteKey,
                ref rootBoundaryComponent,
                ref firstUnprotectedRootLocation,
                ref boundaryRootLocation);
        }

        return new RenderTreeAnalysis(
            hasBoundaryRoot: hasBoundaryProtectedContent && !hasUnprotectedInteractiveRoot,
            boundaryRootHasErrorContent,
            rootBoundaryIsKeyed,
            rootBoundaryUsesStaleRouteKey,
            rootBoundaryComponent,
            childComponents,
            firstUnprotectedRootLocation,
            boundaryRootLocation);
    }

    private static RenderTreeAnalysis AnalyzeStatementBranches(
        IfStatementSyntax ifStatement,
        ImmutableHashSet<INamedTypeSymbol> childComponents,
        SemanticModel semanticModel,
        INamedTypeSymbol errorBoundarySymbol,
        CancellationToken cancellationToken)
    {
        var ifAnalysis = AnalyzeBuildRenderTreeStatements(
            GetBranchStatements(ifStatement.Statement),
            childComponents,
            semanticModel,
            errorBoundarySymbol,
            cancellationToken);

        if (ifStatement.Else is null)
        {
            return ifAnalysis;
        }

        var elseAnalysis = AnalyzeBuildRenderTreeStatements(
            GetBranchStatements(ifStatement.Else.Statement),
            childComponents,
            semanticModel,
            errorBoundarySymbol,
            cancellationToken);

        var hasUnprotectedInteractiveRoot = false;
        var hasBoundaryProtectedContent = false;
        var boundaryRootHasErrorContent = true;
        var rootBoundaryIsKeyed = true;
        var rootBoundaryUsesStaleRouteKey = false;
        INamedTypeSymbol? rootBoundaryComponent = null;
        Location? firstUnprotectedRootLocation = null;
        Location? boundaryRootLocation = null;

        MergeRenderTreeAnalysis(
            ifAnalysis,
            ref hasUnprotectedInteractiveRoot,
            ref hasBoundaryProtectedContent,
            ref boundaryRootHasErrorContent,
            ref rootBoundaryIsKeyed,
            ref rootBoundaryUsesStaleRouteKey,
            ref rootBoundaryComponent,
            ref firstUnprotectedRootLocation,
            ref boundaryRootLocation);
        MergeRenderTreeAnalysis(
            elseAnalysis,
            ref hasUnprotectedInteractiveRoot,
            ref hasBoundaryProtectedContent,
            ref boundaryRootHasErrorContent,
            ref rootBoundaryIsKeyed,
            ref rootBoundaryUsesStaleRouteKey,
            ref rootBoundaryComponent,
            ref firstUnprotectedRootLocation,
            ref boundaryRootLocation);

        return new RenderTreeAnalysis(
            hasBoundaryRoot: hasBoundaryProtectedContent && !hasUnprotectedInteractiveRoot,
            boundaryRootHasErrorContent,
            rootBoundaryIsKeyed,
            rootBoundaryUsesStaleRouteKey,
            rootBoundaryComponent,
            childComponents,
            firstUnprotectedRootLocation,
            boundaryRootLocation);
    }

    private static IEnumerable<StatementSyntax> GetBranchStatements(StatementSyntax statement) =>
        statement is BlockSyntax block ? block.Statements : [statement];

    private static void MergeRenderTreeAnalysis(
        RenderTreeAnalysis analysis,
        ref bool hasUnprotectedInteractiveRoot,
        ref bool hasBoundaryProtectedContent,
        ref bool boundaryRootHasErrorContent,
        ref bool rootBoundaryIsKeyed,
        ref bool rootBoundaryUsesStaleRouteKey,
        ref INamedTypeSymbol? rootBoundaryComponent,
        ref Location? firstUnprotectedRootLocation,
        ref Location? boundaryRootLocation)
    {
        hasUnprotectedInteractiveRoot |= analysis.FirstUnprotectedRootLocation is not null;
        hasBoundaryProtectedContent |= analysis.HasBoundaryRoot || analysis.RootBoundaryComponent is not null;
        boundaryRootHasErrorContent &= analysis.BoundaryRootHasErrorContent;
        if (analysis.HasBoundaryRoot || analysis.RootBoundaryComponent is not null)
        {
            rootBoundaryIsKeyed &= analysis.RootBoundaryIsKeyed;
        }
        rootBoundaryUsesStaleRouteKey |= analysis.RootBoundaryUsesStaleRouteKey;

        rootBoundaryComponent ??= analysis.RootBoundaryComponent;
        firstUnprotectedRootLocation ??= analysis.FirstUnprotectedRootLocation;
        boundaryRootLocation ??= analysis.BoundaryRootLocation;
    }

    private static void MergeRootAnalysisState(
        RootAnalysisState rootAnalysisState,
        ref bool hasUnprotectedInteractiveRoot,
        ref bool hasBoundaryProtectedContent,
        ref bool boundaryRootHasErrorContent,
        ref bool rootBoundaryIsKeyed,
        ref bool rootBoundaryUsesStaleRouteKey,
        ref INamedTypeSymbol? rootBoundaryComponent,
        ref Location? firstUnprotectedRootLocation,
        ref Location? boundaryRootLocation)
    {
        hasUnprotectedInteractiveRoot |= rootAnalysisState.HasUnprotectedInteractiveContent;
        hasBoundaryProtectedContent |= rootAnalysisState.HasBoundaryProtectedContent;
        boundaryRootHasErrorContent &= !rootAnalysisState.HasBoundaryMissingErrorContent;
        rootBoundaryIsKeyed &= rootAnalysisState.RootBoundaryIsKeyed;
        rootBoundaryUsesStaleRouteKey |= rootAnalysisState.RootBoundaryUsesStaleRouteKey;
        rootBoundaryComponent ??= rootAnalysisState.RootBoundaryComponent;
        firstUnprotectedRootLocation ??= rootAnalysisState.FirstUnprotectedContentLocation;
        boundaryRootLocation ??= rootAnalysisState.RootBoundaryLocation;
    }

    private sealed class RootAnalysisState
    {
        private readonly Stack<NodeFrame> nodeStack = new();
        private int activeBoundaryCount;
        private bool rootBoundaryMissingErrorContent;
        private bool rootBoundaryHasErrorContent;
        private bool rootBoundaryIsKeyed;
        private bool rootBoundaryUsesStaleRouteKey;

        private RootAnalysisState(
            bool ignoredRoot,
            bool boundaryRoot,
            INamedTypeSymbol? rootBoundaryComponent,
            bool rootBoundaryHasBuiltInErrorContent,
            Location rootLocation)
        {
            IgnoredRoot = ignoredRoot;
            BoundaryRoot = boundaryRoot;
            RootBoundaryComponent = rootBoundaryComponent;
            RootBoundaryLocation = boundaryRoot ? rootLocation : null;
            rootBoundaryHasErrorContent = rootBoundaryHasBuiltInErrorContent;
            nodeStack.Push(new NodeFrame(boundaryRoot, isComponent: true));
            activeBoundaryCount = boundaryRoot ? 1 : 0;
            HasBoundaryProtectedContent = boundaryRoot;
        }

        public bool BoundaryRoot { get; }

        public bool IgnoredRoot { get; }

        public INamedTypeSymbol? RootBoundaryComponent { get; }

        public bool HasBoundaryProtectedContent { get; private set; }

        public bool HasUnprotectedInteractiveContent { get; private set; }

        public Location? FirstUnprotectedContentLocation { get; private set; }

        public bool HasBoundaryMissingErrorContent => rootBoundaryMissingErrorContent;

        public bool IsComplete => nodeStack.Count == 0;

        public bool RootBoundaryIsKeyed => !BoundaryRoot || rootBoundaryIsKeyed;

        public bool RootBoundaryUsesStaleRouteKey => BoundaryRoot && rootBoundaryUsesStaleRouteKey;

        public Location? RootBoundaryLocation { get; }

        public static RootAnalysisState CreateElementRoot(Location rootLocation) =>
            new(ignoredRoot: false, boundaryRoot: false, rootBoundaryComponent: null, rootBoundaryHasBuiltInErrorContent: false, rootLocation);

        public static RootAnalysisState CreateComponentRoot(
            INamedTypeSymbol? componentType,
            INamedTypeSymbol errorBoundarySymbol,
            CancellationToken cancellationToken,
            Location rootLocation)
        {
            if (IsIgnoredRootComponent(componentType))
            {
                return new RootAnalysisState(ignoredRoot: true, boundaryRoot: false, rootBoundaryComponent: null, rootBoundaryHasBuiltInErrorContent: false, rootLocation);
            }

            if (componentType is not null && componentType.InheritsFromOrEquals(errorBoundarySymbol))
            {
                var hasBuiltInErrorContent = BoundaryTypeHasBuiltInErrorContent(componentType, errorBoundarySymbol, cancellationToken);
                return new RootAnalysisState(ignoredRoot: false, boundaryRoot: true, rootBoundaryComponent: componentType, rootBoundaryHasBuiltInErrorContent: hasBuiltInErrorContent, rootLocation);
            }

            return new RootAnalysisState(ignoredRoot: false, boundaryRoot: false, rootBoundaryComponent: null, rootBoundaryHasBuiltInErrorContent: false, rootLocation);
        }

        public void OpenElement(Location location) => nodeStack.Push(new NodeFrame(isBoundary: false, isComponent: false));

        public void OpenComponent(INamedTypeSymbol? componentType, INamedTypeSymbol errorBoundarySymbol, Location location)
        {
            if (IgnoredRoot || IsIgnoredRootComponent(componentType))
            {
                nodeStack.Push(new NodeFrame(isBoundary: false, isComponent: true));
                return;
            }

            var isBoundary = componentType is not null && componentType.InheritsFromOrEquals(errorBoundarySymbol);
            nodeStack.Push(new NodeFrame(isBoundary, isComponent: true));
            if (isBoundary)
            {
                activeBoundaryCount++;
                HasBoundaryProtectedContent = true;
            }
        }

        public void AnalyzeAttribute(InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (IgnoredRoot)
            {
                return;
            }

            if (BoundaryRoot && nodeStack.Count == 1 && HasAttributeNamed(invocation, semanticModel, cancellationToken, "ErrorContent"))
            {
                rootBoundaryHasErrorContent = true;
                return;
            }

            if (activeBoundaryCount != 0 || nodeStack.Count == 0)
            {
                return;
            }

            var currentNode = nodeStack.Peek();
            if (!currentNode.IsComponent)
            {
                if (HasEventAttribute(invocation, semanticModel, cancellationToken))
                {
                    MarkUnprotectedInteractiveContent(invocation.GetLocation());
                }

                return;
            }

            if (HasCallbackLikeComponentAttribute(invocation, semanticModel, cancellationToken))
            {
                MarkUnprotectedInteractiveContent(invocation.GetLocation());
            }
        }

        public void AnalyzeSetKey(
            InvocationExpressionSyntax invocation,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            if (BoundaryRoot && nodeStack.Count == 1)
            {
                rootBoundaryIsKeyed = true;
                if (invocation.ArgumentList.Arguments.Count > 0)
                {
                    rootBoundaryUsesStaleRouteKey = IsStaleLayoutBoundaryKeyExpression(
                        invocation.ArgumentList.Arguments[invocation.ArgumentList.Arguments.Count - 1].Expression,
                        semanticModel,
                        cancellationToken);
                }
            }
        }

        public void CloseNode()
        {
            if (nodeStack.Count == 0)
            {
                return;
            }

            if (BoundaryRoot && nodeStack.Count == 1 && !rootBoundaryHasErrorContent)
            {
                rootBoundaryMissingErrorContent = true;
            }

            if (nodeStack.Pop().IsBoundary)
            {
                activeBoundaryCount--;
            }
        }

        private void MarkUnprotectedInteractiveContent(Location location)
        {
            HasUnprotectedInteractiveContent = true;
            FirstUnprotectedContentLocation ??= location;
        }

        private readonly struct NodeFrame
        {
            public NodeFrame(bool isBoundary, bool isComponent)
            {
                IsBoundary = isBoundary;
                IsComponent = isComponent;
            }

            public bool IsBoundary { get; }

            public bool IsComponent { get; }
        }
    }

    private static bool IsIgnoredRootComponent(INamedTypeSymbol? componentType)
    {
        if (componentType is null)
        {
            return false;
        }

        var metadataName = componentType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        return metadataName is "Microsoft.AspNetCore.Components.Web.PageTitle" or
            "Microsoft.AspNetCore.Components.Web.HeadContent";
    }

    private static bool BoundaryTypeHasBuiltInErrorContent(
        INamedTypeSymbol boundaryType,
        INamedTypeSymbol errorBoundarySymbol,
        CancellationToken cancellationToken)
    {
        if (SymbolEqualityComparer.Default.Equals(boundaryType, errorBoundarySymbol))
        {
            return false;
        }

        foreach (var buildRenderTreeMethod in boundaryType.GetMembers("BuildRenderTree").OfType<IMethodSymbol>())
        {
            foreach (var syntaxReference in buildRenderTreeMethod.DeclaringSyntaxReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (syntaxReference.GetSyntax(cancellationToken) is not MethodDeclarationSyntax methodDeclaration ||
                    methodDeclaration.Body is null)
                {
                    continue;
                }

                if (methodDeclaration.Body.DescendantNodesAndSelf().OfType<ExpressionSyntax>().Any(expression =>
                    expression switch
                    {
                        IdentifierNameSyntax identifier => identifier.Identifier.ValueText is "CurrentException" or "ErrorContent",
                        MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText is "CurrentException" or "ErrorContent",
                        _ => false
                    }))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasEventAttribute(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.Count < 2)
        {
            return false;
        }

        var constantValue = TryGetConstantValue(invocation.ArgumentList.Arguments[1].Expression, semanticModel, cancellationToken);
        return constantValue.HasValue &&
            constantValue.Value is string attributeName &&
            attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAttributeNamed(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        string attributeName)
    {
        if (invocation.ArgumentList.Arguments.Count < 2)
        {
            return false;
        }

        var constantValue = TryGetConstantValue(invocation.ArgumentList.Arguments[1].Expression, semanticModel, cancellationToken);
        return constantValue.HasValue &&
               constantValue.Value is string value &&
               string.Equals(value, attributeName, StringComparison.Ordinal);
    }

    private static bool HasCallbackLikeComponentAttribute(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.Count < 3)
        {
            return false;
        }

        var valueExpression = invocation.ArgumentList.Arguments[2].Expression;
        if (valueExpression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
        {
            return true;
        }

        var typeInfo = TryGetTypeInfo(valueExpression, semanticModel, cancellationToken);
        var type = typeInfo?.ConvertedType ?? typeInfo?.Type;

        if (type is not INamedTypeSymbol namedType)
        {
            return false;
        }

        if (namedType.TypeKind == TypeKind.Delegate)
        {
            return true;
        }

        var metadataName = namedType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        return metadataName is "Microsoft.AspNetCore.Components.EventCallback" or
            "Microsoft.AspNetCore.Components.EventCallback<TValue>" or
            "Microsoft.AspNetCore.Components.RenderFragment" or
            "Microsoft.AspNetCore.Components.RenderFragment<TValue>";
    }

    private static IEnumerable<IMethodSymbol> GetBuildRenderTreeReferencedMethods(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        if (methodDeclaration.Body is null)
        {
            yield break;
        }

        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var expression in methodDeclaration.Body.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
        {
            var symbol = TryGetSymbol(expression, semanticModel, cancellationToken);

            if (symbol is not IMethodSymbol methodSymbol)
            {
                continue;
            }

            methodSymbol = NormalizeMethodSymbol(methodSymbol);
            if (!IsRelevantMethod(methodSymbol, containingType) || !seen.Add(methodSymbol))
            {
                continue;
            }

            yield return methodSymbol;
        }
    }

    private static IEnumerable<INamedTypeSymbol> GetReferencedComponentBuildRenderTrees(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        INamedTypeSymbol componentBaseSymbol,
        CancellationToken cancellationToken)
    {
        if (methodDeclaration.Body is null)
        {
            yield break;
        }

        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var expression in methodDeclaration.Body.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
        {
            var symbol = TryGetSymbol(expression, semanticModel, cancellationToken);

            if (symbol is not IMethodSymbol methodSymbol)
            {
                continue;
            }

            methodSymbol = NormalizeMethodSymbol(methodSymbol);
            if (methodSymbol.Name != "BuildRenderTree")
            {
                continue;
            }

            var referencedComponent = NormalizeComponentSymbol(methodSymbol.ContainingType);
            if (!IsComponent(referencedComponent, componentBaseSymbol) ||
                SymbolEqualityComparer.Default.Equals(referencedComponent, NormalizeComponentSymbol(containingType)) ||
                !seen.Add(referencedComponent))
            {
                continue;
            }

            yield return referencedComponent;
        }
    }

    private static IEnumerable<IMethodSymbol> GetCalledMemberMethods(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        var rootNode = GetMethodExecutableRoot(methodDeclaration);
        if (rootNode is null)
        {
            yield break;
        }

        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var invocation in rootNode.DescendantNodesAndSelf(static node => !IsNestedFunctionLike(node)).OfType<InvocationExpressionSyntax>())
        {
            var methodSymbol = GetInvokedMemberMethod(invocation, semanticModel, containingType, cancellationToken);
            if (methodSymbol is null || !seen.Add(methodSymbol))
            {
                continue;
            }

            yield return methodSymbol;
        }
    }

    private static IMethodSymbol? GetDelegatedMethod(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        if (methodDeclaration.ExpressionBody is not null)
        {
            return GetInvokedMemberMethod(methodDeclaration.ExpressionBody.Expression, semanticModel, containingType, cancellationToken);
        }

        if (methodDeclaration.Body is null || methodDeclaration.Body.Statements.Count != 1)
        {
            return null;
        }

        return methodDeclaration.Body.Statements[0] switch
        {
            ExpressionStatementSyntax expressionStatement => GetInvokedMemberMethod(expressionStatement.Expression, semanticModel, containingType, cancellationToken),
            ReturnStatementSyntax { Expression: { } expression } => GetInvokedMemberMethod(expression, semanticModel, containingType, cancellationToken),
            _ => null
        };
    }

    private static IMethodSymbol? GetInvokedMemberMethod(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        while (expression is AwaitExpressionSyntax awaitExpression)
        {
            expression = awaitExpression.Expression;
        }

        if (expression is not InvocationExpressionSyntax invocation)
        {
            return null;
        }

        var symbol = TryGetSymbol(invocation, semanticModel, cancellationToken);

        if (symbol is not IMethodSymbol methodSymbol)
        {
            return null;
        }

        methodSymbol = NormalizeMethodSymbol(methodSymbol);
        return IsRelevantMethod(methodSymbol, containingType) ? methodSymbol : null;
    }

    private static IEnumerable<INamedTypeSymbol> GetRenderedChildComponents(
        BlockSyntax body,
        SemanticModel semanticModel,
        INamedTypeSymbol componentBaseSymbol,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "OpenComponent" })
            {
                continue;
            }

            var childComponent = TryGetComponentType(invocation, semanticModel, cancellationToken);
            if (childComponent is null ||
                !IsComponent(childComponent, componentBaseSymbol) ||
                IsIgnoredRootComponent(childComponent) ||
                !seen.Add(childComponent))
            {
                continue;
            }

            yield return childComponent;
        }
    }

    private static IEnumerable<InvocationExpressionSyntax> GetJsInteropInvocations(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var rootNode = GetMethodExecutableRoot(methodDeclaration);
        if (rootNode is null)
        {
            yield break;
        }

        foreach (var invocation in rootNode.DescendantNodesAndSelf(static node => !IsNestedFunctionLike(node)).OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var memberName } &&
                memberName is "InvokeAsync" or "InvokeVoidAsync" or "DisposeAsync")
            {
                yield return invocation;
                continue;
            }

            var symbol = TryGetSymbol(invocation, semanticModel, cancellationToken);
            if (symbol is IMethodSymbol methodSymbol && IsJsInteropMethod(methodSymbol))
            {
                yield return invocation;
            }
        }
    }

    private static bool IsJsInteropMethod(IMethodSymbol methodSymbol)
    {
        methodSymbol = methodSymbol.OriginalDefinition;
        var relevantTypes = ImmutableArray.CreateBuilder<INamedTypeSymbol>();
        relevantTypes.Add(methodSymbol.ContainingType);

        if (methodSymbol.ReducedFrom is IMethodSymbol reducedFrom &&
            reducedFrom.Parameters.Length > 0 &&
            reducedFrom.Parameters[0].Type is INamedTypeSymbol receiverType)
        {
            relevantTypes.Add(receiverType);
        }
        else if (methodSymbol.Parameters.Length > 0 &&
                 methodSymbol.IsExtensionMethod &&
                 methodSymbol.Parameters[0].Type is INamedTypeSymbol extensionReceiverType)
        {
            relevantTypes.Add(extensionReceiverType);
        }

        var metadataNames = relevantTypes
            .SelectMany(GetTypeMetadataNames)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (methodSymbol.Name is "InvokeAsync" or "InvokeVoidAsync")
        {
            return metadataNames.Any(static name =>
                name is "Microsoft.JSInterop.IJSRuntime" or
                    "Microsoft.JSInterop.IJSInProcessRuntime" or
                    "Microsoft.JSInterop.IJSUnmarshalledRuntime" or
                    "Microsoft.JSInterop.IJSObjectReference" or
                    "Microsoft.JSInterop.IJSInProcessObjectReference");
        }

        if (methodSymbol.Name == "DisposeAsync")
        {
            return metadataNames.Any(static name =>
                name is "Microsoft.JSInterop.IJSObjectReference" or
                    "Microsoft.JSInterop.IJSInProcessObjectReference");
        }

        return false;
    }

    private static IEnumerable<string> GetTypeMetadataNames(INamedTypeSymbol typeSymbol)
    {
        yield return typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        foreach (var interfaceType in typeSymbol.AllInterfaces)
        {
            yield return interfaceType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        }
    }

    private static bool IsWithinInteractivityGuard(SyntaxNode node)
    {
        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is MethodDeclarationSyntax)
            {
                return false;
            }

            if (current is IfStatementSyntax ifStatement && HasInteractivityGuard(ifStatement.Condition))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasInteractivityGuard(ExpressionSyntax condition)
    {
        foreach (var token in condition.DescendantTokens())
        {
            var text = token.ValueText;
            if (text.IndexOf("Interactive", StringComparison.OrdinalIgnoreCase) >= 0 ||
                string.Equals(text, "AssignedRenderMode", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "IsConnected", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ImmutableArray<Location> GetCatchWithoutLoggingLocations(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (methodDeclaration.Body is null)
        {
            return [];
        }

        var locations = ImmutableArray.CreateBuilder<Location>();
        foreach (var catchClause in methodDeclaration.Body.DescendantNodes(static node => !IsNestedFunctionLike(node)).OfType<CatchClauseSyntax>())
        {
            if (CatchClauseLogsOrRethrows(catchClause, semanticModel, cancellationToken))
            {
                continue;
            }

            locations.Add(catchClause.GetLocation());
        }

        return locations.ToImmutable();
    }

    private static bool CatchClauseLogsOrRethrows(
        CatchClauseSyntax catchClause,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (catchClause.Block.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any())
        {
            return true;
        }

        foreach (var invocation in catchClause.Block.DescendantNodesAndSelf(static node => !IsNestedFunctionLike(node)).OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var memberName } &&
                (memberName.StartsWith("Log", StringComparison.Ordinal) || memberName is "TrackException" or "CaptureException" or "ReportException"))
            {
                return true;
            }

            var symbol = TryGetSymbol(invocation, semanticModel, cancellationToken);
            if (symbol is IMethodSymbol methodSymbol && IsLoggingOrTelemetryMethod(methodSymbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLoggingOrTelemetryMethod(IMethodSymbol methodSymbol)
    {
        if (methodSymbol.Name.StartsWith("Log", StringComparison.Ordinal) ||
            methodSymbol.Name is "TrackException" or "CaptureException" or "ReportException")
        {
            return true;
        }

        return GetTypeMetadataNames(methodSymbol.ContainingType).Any(static metadataName =>
            metadataName is "Microsoft.Extensions.Logging.ILogger" or "Microsoft.Extensions.Logging.LoggerExtensions");
    }

    private static SyntaxNode? GetMethodExecutableRoot(MethodDeclarationSyntax methodDeclaration) =>
        methodDeclaration.ExpressionBody is not null
            ? methodDeclaration.ExpressionBody.Expression
            : methodDeclaration.Body;

    private static INamedTypeSymbol? TryGetComponentType(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name: GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } genericName
            })
        {
            return null;
        }

        return TryGetTypeInfo(genericName.TypeArgumentList.Arguments[0], semanticModel, cancellationToken)?.Type as INamedTypeSymbol;
    }

    private static bool IsStaleLayoutBoundaryKeyExpression(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var visitedSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        return AnalyzeLayoutBoundaryKeyExpression(expression, semanticModel, cancellationToken, visitedSymbols) ==
            LayoutBoundaryKeyAnalysis.StaleNavigationSnapshot;
    }

    private static LayoutBoundaryKeyAnalysis AnalyzeLayoutBoundaryKeyExpression(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken,
        HashSet<ISymbol> visitedSymbols)
    {
        if (IsNavigationReactiveRouteExpression(expression))
        {
            return LayoutBoundaryKeyAnalysis.NavigationReactive;
        }

        var symbol = TryGetSymbol(expression, semanticModel, cancellationToken);
        if (symbol is null || !visitedSymbols.Add(symbol))
        {
            return LayoutBoundaryKeyAnalysis.Unknown;
        }

        try
        {
            return symbol switch
            {
                IPropertySymbol propertySymbol => AnalyzeLayoutBoundaryKeyProperty(propertySymbol, cancellationToken, visitedSymbols),
                IFieldSymbol fieldSymbol => AnalyzeLayoutBoundaryKeyField(fieldSymbol, cancellationToken),
                _ => LayoutBoundaryKeyAnalysis.Unknown
            };
        }
        finally
        {
            visitedSymbols.Remove(symbol);
        }
    }

    private static LayoutBoundaryKeyAnalysis AnalyzeLayoutBoundaryKeyProperty(
        IPropertySymbol propertySymbol,
        CancellationToken cancellationToken,
        HashSet<ISymbol> visitedSymbols)
    {
        foreach (var syntaxReference in propertySymbol.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax(cancellationToken) is not PropertyDeclarationSyntax propertyDeclaration)
            {
                continue;
            }

            var expression = propertyDeclaration.ExpressionBody?.Expression ??
                propertyDeclaration.AccessorList?.Accessors
                    .FirstOrDefault(static accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration))?
                    .Body?
                    .Statements
                    .OfType<ReturnStatementSyntax>()
                    .FirstOrDefault()?
                    .Expression;

            if (expression is null)
            {
                continue;
            }

            if (IsNavigationReactiveRouteExpression(expression))
            {
                return LayoutBoundaryKeyAnalysis.NavigationReactive;
            }

            if (TryResolveReferencedField(propertySymbol.ContainingType, expression) is { } referencedField)
            {
                var analysis = AnalyzeLayoutBoundaryKeyField(referencedField, cancellationToken);
                if (analysis is LayoutBoundaryKeyAnalysis.NavigationReactive or LayoutBoundaryKeyAnalysis.StaleNavigationSnapshot)
                {
                    return analysis;
                }
            }

        }

        return LayoutBoundaryKeyAnalysis.Unknown;
    }

    private static LayoutBoundaryKeyAnalysis AnalyzeLayoutBoundaryKeyField(
        IFieldSymbol fieldSymbol,
        CancellationToken cancellationToken)
    {
        var hasAssignment = false;
        foreach (var syntaxReference in fieldSymbol.ContainingType.DeclaringSyntaxReferences)
        {
            var typeDeclaration = syntaxReference.GetSyntax(cancellationToken);

            foreach (var assignment in typeDeclaration.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!IsFieldAssignmentTarget(assignment.Left, fieldSymbol))
                {
                    continue;
                }

                hasAssignment = true;
                if (assignment.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not { } methodDeclaration ||
                    methodDeclaration.Identifier.ValueText is not ("OnInitialized" or "OnInitializedAsync") ||
                    !IsNavigationReactiveRouteExpression(assignment.Right))
                {
                    return LayoutBoundaryKeyAnalysis.Unknown;
                }
            }
        }

        return hasAssignment
            ? LayoutBoundaryKeyAnalysis.StaleNavigationSnapshot
            : LayoutBoundaryKeyAnalysis.Unknown;
    }

    private static IFieldSymbol? TryResolveReferencedField(INamedTypeSymbol containingType, ExpressionSyntax expression)
    {
        var referencedName = expression switch
        {
            IdentifierNameSyntax identifierName => identifierName.Identifier.ValueText,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            _ => null
        };

        return referencedName is null
            ? null
            : containingType.GetMembers(referencedName).OfType<IFieldSymbol>().FirstOrDefault();
    }

    private static bool IsFieldAssignmentTarget(ExpressionSyntax left, IFieldSymbol fieldSymbol) =>
        left switch
        {
            IdentifierNameSyntax identifierName => identifierName.Identifier.ValueText == fieldSymbol.Name,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText == fieldSymbol.Name,
            _ => false
        };

    private static bool IsNavigationReactiveRouteExpression(ExpressionSyntax expression) =>
        expression switch
        {
            InvocationExpressionSyntax invocation => IsNavigationRouteInvocation(invocation),
            MemberAccessExpressionSyntax memberAccess => IsNavigationUriMemberAccess(memberAccess),
            _ => false
        };

    private static bool IsNavigationUriMemberAccess(MemberAccessExpressionSyntax memberAccess)
    {
        if (memberAccess.Name.Identifier.ValueText != "Uri")
        {
            return false;
        }

        var receiverText = memberAccess.Expression.ToString();
        return receiverText.IndexOf("nav", StringComparison.OrdinalIgnoreCase) >= 0 ||
            receiverText.IndexOf("navigation", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsNavigationRouteInvocation(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            MemberAccessExpressionSyntax memberAccess when memberAccess.Name.Identifier.ValueText == "ToBaseRelativePath" &&
                invocation.ArgumentList.Arguments.Count > 0 =>
                IsNavigationReactiveRouteExpression(invocation.ArgumentList.Arguments[0].Expression),
            IdentifierNameSyntax identifierName when identifierName.Identifier.ValueText == "ToBaseRelativePath" &&
                invocation.ArgumentList.Arguments.Count > 0 =>
                IsNavigationReactiveRouteExpression(invocation.ArgumentList.Arguments[0].Expression),
            _ => false
        };

    private static Optional<object?> TryGetConstantValue(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(expression.SyntaxTree, semanticModel.SyntaxTree))
        {
            return default;
        }

        try
        {
            return semanticModel.GetConstantValue(expression, cancellationToken);
        }
        catch (ArgumentException)
        {
            return default;
        }
    }

    private static TypeInfo? TryGetTypeInfo(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(expression.SyntaxTree, semanticModel.SyntaxTree))
        {
            return null;
        }

        try
        {
            return semanticModel.GetTypeInfo(expression, cancellationToken);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ISymbol? TryGetSymbol(
        SyntaxNode node,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(node.SyntaxTree, semanticModel.SyntaxTree))
        {
            return null;
        }

        try
        {
            var symbolInfo = semanticModel.GetSymbolInfo(node, cancellationToken);
            return symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IMethodSymbol? TryGetDeclaredMethodSymbol(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(methodDeclaration.SyntaxTree, semanticModel.SyntaxTree))
        {
            return null;
        }

        try
        {
            return semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken) as IMethodSymbol;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IMethodSymbol NormalizeMethodSymbol(IMethodSymbol methodSymbol) => methodSymbol.OriginalDefinition;

    private static INamedTypeSymbol NormalizeComponentSymbol(INamedTypeSymbol componentSymbol) => componentSymbol.OriginalDefinition;

    private static bool MethodContainsTryCatch(MethodDeclarationSyntax methodDeclaration)
    {
        if (methodDeclaration.Body is null)
        {
            return false;
        }

        foreach (var tryStatement in methodDeclaration.Body.DescendantNodes(static node => !IsNestedFunctionLike(node)).OfType<TryStatementSyntax>())
        {
            if (tryStatement.Catches.Count > 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNestedFunctionLike(SyntaxNode node) =>
        node is AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax;

    private sealed class BoundaryCoverageResolver
    {
        private readonly Compilation compilation;
        private readonly INamedTypeSymbol componentBaseSymbol;
        private readonly INamedTypeSymbol errorBoundarySymbol;
        private readonly INamedTypeSymbol renderModeAttributeSymbol;
        private readonly ImmutableDictionary<string, AdditionalText> razorAdditionalFiles;
        private readonly ConcurrentDictionary<string, RazorMarkupAnalysis?> razorMarkupCache;
        private readonly object gate = new();
        private bool initialized;
        private HashSet<INamedTypeSymbol> relevantComponents = new(SymbolEqualityComparer.Default);
        private HashSet<INamedTypeSymbol> boundaryProtectedComponents = new(SymbolEqualityComparer.Default);

        public BoundaryCoverageResolver(
            Compilation compilation,
            INamedTypeSymbol componentBaseSymbol,
            INamedTypeSymbol errorBoundarySymbol,
            INamedTypeSymbol renderModeAttributeSymbol,
            ImmutableDictionary<string, AdditionalText> razorAdditionalFiles,
            ConcurrentDictionary<string, RazorMarkupAnalysis?> razorMarkupCache)
        {
            this.compilation = compilation;
            this.componentBaseSymbol = componentBaseSymbol;
            this.errorBoundarySymbol = errorBoundarySymbol;
            this.renderModeAttributeSymbol = renderModeAttributeSymbol;
            this.razorAdditionalFiles = razorAdditionalFiles;
            this.razorMarkupCache = razorMarkupCache;
        }

        public bool IsBoundaryProtected(INamedTypeSymbol componentSymbol, CancellationToken cancellationToken)
        {
            EnsureInitialized(cancellationToken);
            return boundaryProtectedComponents.Contains(componentSymbol);
        }

        public bool IsRelevantComponent(INamedTypeSymbol componentSymbol, CancellationToken cancellationToken)
        {
            EnsureInitialized(cancellationToken);
            return relevantComponents.Contains(componentSymbol);
        }

        private void EnsureInitialized(CancellationToken cancellationToken)
        {
            if (initialized)
            {
                return;
            }

            lock (gate)
            {
                if (initialized)
                {
                    return;
                }

                var allComponentSymbols = GetAllNamedTypes(compilation.Assembly.GlobalNamespace)
                    .Where(symbol => IsComponent(symbol, componentBaseSymbol))
                    .ToArray();
                var allComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
                var declaredRenderModes = new ConcurrentDictionary<INamedTypeSymbol, string?>(SymbolEqualityComparer.Default);
                var localBoundaryComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
                var localRelevantBoundaryComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
                var componentOwners = new ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>>(SymbolEqualityComparer.Default);
                var boundaryComponentNames = allComponentSymbols
                    .Concat(GetAllNamedTypes(compilation.Assembly.GlobalNamespace).Where(symbol => symbol.InheritsFromOrEquals(errorBoundarySymbol)))
                    .Select(symbol => symbol.Name)
                    .ToImmutableHashSet(StringComparer.Ordinal);

                foreach (var component in allComponentSymbols)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    allComponents.TryAdd(component, 0);

                    var declaredRenderMode = GetDeclaredRenderModeKey(component, renderModeAttributeSymbol);
                    declaredRenderModes.TryAdd(component, declaredRenderMode);

                    var renderTreeAnalysis = AnalyzeComponentRenderTree(component, boundaryComponentNames, cancellationToken);
                    if (renderTreeAnalysis.HasBoundaryRoot ||
                        renderTreeAnalysis.FirstUnprotectedRootLocation is not null ||
                        renderTreeAnalysis.ChildComponents.Count > 0)
                    {
                        localRelevantBoundaryComponents.TryAdd(component, 0);
                    }

                    if (renderTreeAnalysis.HasBoundaryRoot)
                    {
                        localBoundaryComponents.TryAdd(component, 0);
                    }

                    foreach (var childComponent in renderTreeAnalysis.ChildComponents)
                    {
                        allComponents.TryAdd(childComponent, 0);
                        var owners = componentOwners.GetOrAdd(childComponent, static _ => new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default));
                        owners.TryAdd(component, 0);
                    }
                }

                var effectiveRenderModes = ComputeEffectiveRenderModes(allComponents.Keys, declaredRenderModes, componentOwners);
                relevantComponents = ComputeRelevantBoundaryComponents(effectiveRenderModes, componentOwners, localRelevantBoundaryComponents);
                var boundaryProtectedRenderModes = ComputeBoundaryProtectedRenderModes(relevantComponents, effectiveRenderModes, localBoundaryComponents, componentOwners);
                boundaryProtectedComponents = ComputeBoundaryProtectedComponents(relevantComponents, effectiveRenderModes, boundaryProtectedRenderModes);
                initialized = true;
            }
        }

        private RenderTreeAnalysis AnalyzeComponentRenderTree(
            INamedTypeSymbol componentSymbol,
            ImmutableHashSet<string> boundaryComponentNames,
            CancellationToken cancellationToken)
        {
            var childComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var hasBoundaryRoot = false;
            var boundaryRootHasErrorContent = true;
            var rootBoundaryIsKeyed = true;
            var rootBoundaryUsesStaleRouteKey = false;
            INamedTypeSymbol? rootBoundaryComponent = null;
            Location? firstUnprotectedRootLocation = null;
            Location? boundaryRootLocation = null;

            foreach (var syntaxReference in componentSymbol.GetMembers("BuildRenderTree").OfType<IMethodSymbol>().SelectMany(static method => method.DeclaringSyntaxReferences))
            {
                if (syntaxReference.GetSyntax(cancellationToken) is not MethodDeclarationSyntax methodDeclaration ||
                    methodDeclaration.Body is null)
                {
                    continue;
                }

                var semanticModel = compilation.GetSemanticModel(methodDeclaration.SyntaxTree);
                var generatedAnalysis = AnalyzeBuildRenderTree(methodDeclaration.Body, semanticModel, componentBaseSymbol, errorBoundarySymbol, cancellationToken);
                var declaredMethod = TryGetDeclaredMethodSymbol(methodDeclaration, semanticModel, cancellationToken);
                var razorAnalysis = declaredMethod is null
                    ? null
                    : TryGetRazorMarkupAnalysis(
                        methodDeclaration,
                        NormalizeMethodSymbol(declaredMethod),
                        boundaryComponentNames,
                        razorAdditionalFiles,
                        razorMarkupCache,
                        cancellationToken);
                var combinedRootAnalysis = CombineRootAnalysis(
                    generatedAnalysis,
                    razorAnalysis,
                    componentSymbol.GetPreferredSourceLocation());

                hasBoundaryRoot |= combinedRootAnalysis.HasBoundaryRoot;
                boundaryRootHasErrorContent &= combinedRootAnalysis.BoundaryRootHasErrorContent;
                if (combinedRootAnalysis.HasBoundaryRoot || generatedAnalysis.RootBoundaryComponent is not null)
                {
                    rootBoundaryIsKeyed &= combinedRootAnalysis.RootBoundaryIsKeyed;
                }
                rootBoundaryUsesStaleRouteKey |= combinedRootAnalysis.RootBoundaryUsesStaleRouteKey;

                rootBoundaryComponent ??= generatedAnalysis.RootBoundaryComponent;
                firstUnprotectedRootLocation ??= combinedRootAnalysis.FirstUnprotectedRootLocation;
                boundaryRootLocation ??= combinedRootAnalysis.BoundaryRootLocation;

                foreach (var childComponent in generatedAnalysis.ChildComponents)
                {
                    childComponents.Add(childComponent);
                }
            }

            return new RenderTreeAnalysis(
                hasBoundaryRoot,
                boundaryRootHasErrorContent,
                rootBoundaryIsKeyed,
                rootBoundaryUsesStaleRouteKey,
                rootBoundaryComponent,
                childComponents.ToImmutable(),
                firstUnprotectedRootLocation,
                boundaryRootLocation);
        }

        private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamespaceSymbol namespaceSymbol)
        {
            foreach (var member in namespaceSymbol.GetMembers())
            {
                if (member is INamespaceSymbol nestedNamespace)
                {
                    foreach (var nestedType in GetAllNamedTypes(nestedNamespace))
                    {
                        yield return nestedType;
                    }

                    continue;
                }

                if (member is INamedTypeSymbol namedType)
                {
                    foreach (var nestedType in GetAllNamedTypes(namedType))
                    {
                        yield return nestedType;
                    }
                }
            }
        }

        private static IEnumerable<INamedTypeSymbol> GetAllNamedTypes(INamedTypeSymbol namedType)
        {
            yield return namedType;
            foreach (var nestedType in namedType.GetTypeMembers())
            {
                foreach (var descendant in GetAllNamedTypes(nestedType))
                {
                    yield return descendant;
                }
            }
        }
    }

    private sealed class MethodAnalysis
    {
        public MethodAnalysis(
            bool hasTryCatch,
            ImmutableHashSet<IMethodSymbol> callees,
            IMethodSymbol? delegatedMethod,
            bool isApiRootCandidate,
            bool isLifecycleMethod,
            bool isDisposeMethod,
            bool hasOperationalCode,
            bool hasJsInteropCalls,
            bool hasUnsafeLifecycleJsInterop,
            bool isAsyncVoid,
            ImmutableArray<Location> catchWithoutLoggingLocations)
        {
            HasTryCatch = hasTryCatch;
            Callees = callees;
            DelegatedMethod = delegatedMethod;
            IsApiRootCandidate = isApiRootCandidate;
            IsLifecycleMethod = isLifecycleMethod;
            IsDisposeMethod = isDisposeMethod;
            HasOperationalCode = hasOperationalCode;
            HasJsInteropCalls = hasJsInteropCalls;
            HasUnsafeLifecycleJsInterop = hasUnsafeLifecycleJsInterop;
            IsAsyncVoid = isAsyncVoid;
            CatchWithoutLoggingLocations = catchWithoutLoggingLocations;
        }

        public bool HasTryCatch { get; }

        public ImmutableHashSet<IMethodSymbol> Callees { get; }

        public IMethodSymbol? DelegatedMethod { get; }

        public bool IsApiRootCandidate { get; }

        public bool IsLifecycleMethod { get; }

        public bool IsDisposeMethod { get; }

        public bool HasOperationalCode { get; }

        public bool HasJsInteropCalls { get; }

        public bool HasUnsafeLifecycleJsInterop { get; }

        public bool IsAsyncVoid { get; }

        public ImmutableArray<Location> CatchWithoutLoggingLocations { get; }
    }

    private sealed class RenderTreeAnalysis
    {
        public RenderTreeAnalysis(
            bool hasBoundaryRoot,
            bool boundaryRootHasErrorContent,
            bool rootBoundaryIsKeyed,
            bool rootBoundaryUsesStaleRouteKey,
            INamedTypeSymbol? rootBoundaryComponent,
            ImmutableHashSet<INamedTypeSymbol> childComponents,
            Location? firstUnprotectedRootLocation,
            Location? boundaryRootLocation)
        {
            HasBoundaryRoot = hasBoundaryRoot;
            BoundaryRootHasErrorContent = boundaryRootHasErrorContent;
            RootBoundaryIsKeyed = rootBoundaryIsKeyed;
            RootBoundaryUsesStaleRouteKey = rootBoundaryUsesStaleRouteKey;
            RootBoundaryComponent = rootBoundaryComponent;
            ChildComponents = childComponents;
            FirstUnprotectedRootLocation = firstUnprotectedRootLocation;
            BoundaryRootLocation = boundaryRootLocation;
        }

        public bool HasBoundaryRoot { get; }

        public bool BoundaryRootHasErrorContent { get; }

        public bool RootBoundaryIsKeyed { get; }

        public bool RootBoundaryUsesStaleRouteKey { get; }

        public INamedTypeSymbol? RootBoundaryComponent { get; }

        public ImmutableHashSet<INamedTypeSymbol> ChildComponents { get; }

        public Location? FirstUnprotectedRootLocation { get; }

        public Location? BoundaryRootLocation { get; }
    }

    private sealed class CombinedRootAnalysis
    {
        public CombinedRootAnalysis(
            bool hasBoundaryRoot,
            bool boundaryRootHasErrorContent,
            bool rootBoundaryIsKeyed,
            bool rootBoundaryUsesStaleRouteKey,
            Location? firstUnprotectedRootLocation,
            Location? boundaryRootLocation)
        {
            HasBoundaryRoot = hasBoundaryRoot;
            BoundaryRootHasErrorContent = boundaryRootHasErrorContent;
            RootBoundaryIsKeyed = rootBoundaryIsKeyed;
            RootBoundaryUsesStaleRouteKey = rootBoundaryUsesStaleRouteKey;
            FirstUnprotectedRootLocation = firstUnprotectedRootLocation;
            BoundaryRootLocation = boundaryRootLocation;
        }

        public bool HasBoundaryRoot { get; }

        public bool BoundaryRootHasErrorContent { get; }

        public bool RootBoundaryIsKeyed { get; }

        public bool RootBoundaryUsesStaleRouteKey { get; }

        public Location? FirstUnprotectedRootLocation { get; }

        public Location? BoundaryRootLocation { get; }
    }

    private sealed class ComponentDiagnosticLocations
    {
        public ComponentDiagnosticLocations(Location? missingErrorBoundaryLocation, Location? boundaryLocation)
        {
            MissingErrorBoundaryLocation = missingErrorBoundaryLocation;
            BoundaryLocation = boundaryLocation;
        }

        public Location? MissingErrorBoundaryLocation { get; }

        public Location? BoundaryLocation { get; }
    }

    private enum LayoutBoundaryKeyAnalysis
    {
        Unknown,
        NavigationReactive,
        StaleNavigationSnapshot
    }
}
