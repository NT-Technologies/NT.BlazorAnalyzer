using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace NT.BlazorAnalyzer;

/// <summary>
/// Analyzes interactive Blazor components for error handling and recovery issues.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlazorErrorHandlingAnalyzer : DiagnosticAnalyzer
{
    private const string StaticRenderModeKey = "<static>";

    /// <inheritdoc />
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
        DiagnosticDescriptors.LayoutBoundaryShouldBeRouteKeyed
    ];

    /// <inheritdoc />
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
            foreach (var boundaryComponentName in GetBoundaryComponentNames(compilationStartContext.Compilation, errorBoundarySymbol))
            {
                boundaryComponentNames.TryAdd(boundaryComponentName, 0);
            }

            var componentRenderAnalyses = new ConcurrentDictionary<INamedTypeSymbol, ComponentRenderAnalysis>(SymbolEqualityComparer.Default);
            var locallyReportedMissingErrorBoundaryComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var locallyReportedBoundaryMissingErrorContentComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var locallyReportedMissingTryCatchMethods = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            var globalInteractiveRoutesOwners = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
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
                        errorBoundarySymbol,
                        componentSymbol,
                        localBuildRenderTreeRootMethods,
                        localMethodAnalyses),
                    SyntaxKind.MethodDeclaration);

                symbolStartContext.RegisterSymbolEndAction(symbolEndContext =>
                {
                    ReportLocalLifecycleTryCatchDiagnostics(
                        symbolEndContext,
                        componentSymbol,
                        localMethodAnalyses,
                        boundaryCoverageResolver);

                    ReportLocalLifecycleJsInteropGuardDiagnostics(
                        symbolEndContext,
                        componentSymbol,
                        localMethodAnalyses);

                    ReportLocalDisposeTryCatchDiagnostics(
                        symbolEndContext,
                        componentSymbol,
                        localMethodAnalyses);

                    ReportLocalMissingTryCatchDiagnostics(
                        symbolEndContext,
                        componentSymbol,
                        localBuildRenderTreeRootMethods,
                        localMethodAnalyses,
                        locallyReportedMissingTryCatchMethods,
                        boundaryCoverageResolver);
                });
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
                    componentRenderAnalyses,
                    globalInteractiveRoutesOwners,
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
                    componentRenderAnalyses,
                    globalInteractiveRoutesOwners,
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
        ConcurrentDictionary<INamedTypeSymbol, ComponentRenderAnalysis> componentRenderAnalyses,
        ConcurrentDictionary<INamedTypeSymbol, byte> globalInteractiveRoutesOwners,
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

            if (HasInteractiveRoutesRenderMode(methodDeclaration.Body, context.SemanticModel, context.CancellationToken))
            {
                globalInteractiveRoutesOwners.TryAdd(containingType, 0);
            }

            var renderTreeAnalysis = AnalyzeBuildRenderTree(
                methodDeclaration.Body,
                context.SemanticModel,
                componentBaseSymbol,
                errorBoundarySymbol,
                context.CancellationToken);
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
            var uncoveredRegions = AttachSourceComponent(
                combinedRootAnalysis.UncoveredRegions,
                containingType);
            var hasUnprotectedRoot = uncoveredRegions.Length > 0;
            var hasRelevantChildren = renderTreeAnalysis.ChildComponents.Count > 0;
            var missingBoundaryLocation = uncoveredRegions.FirstOrDefault().DiagnosticLocation ??
                containingType.GetPreferredSourceLocation();
            var boundaryLocation = combinedRootAnalysis.BoundaryRootLocation ??
                containingType.GetPreferredSourceLocation();

            componentRenderAnalyses[containingType] = new ComponentRenderAnalysis(
                uncoveredRegions,
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

            foreach (var referencedMethod in uncoveredRegions.SelectMany(static region => region.RootMethods))
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

        if (analysis.HasUnhandledJsInteropCalls)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.JsInteropMissingTryCatch,
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
        INamedTypeSymbol errorBoundarySymbol,
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
            if (methodDeclaration.Body is null)
            {
                return;
            }

            var renderTreeAnalysis = AnalyzeBuildRenderTree(
                methodDeclaration.Body,
                context.SemanticModel,
                componentBaseSymbol,
                errorBoundarySymbol,
                context.CancellationToken);
            foreach (var referencedMethod in renderTreeAnalysis.UncoveredRegions.SelectMany(static region => region.RootMethods))
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
            .Where(localBuildRenderTreeRootMethods.ContainsKey)
            .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
            .ToArray();
        var methodsWithSpecificTryCatchDiagnostics = methods
            .Where(method => HasSpecificTryCatchDiagnostic(localMethodAnalyses[method]))
            .ToImmutableHashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        var unsafeMethods = FindUnsafeRootMethods(rootMethods, localMethodAnalyses);

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

    private static void ReportLocalLifecycleTryCatchDiagnostics(
        SymbolAnalysisContext context,
        INamedTypeSymbol componentSymbol,
        ConcurrentDictionary<IMethodSymbol, MethodAnalysis> localMethodAnalyses,
        BoundaryCoverageResolver boundaryCoverageResolver)
    {
        if (boundaryCoverageResolver.IsLifecycleBoundaryProtected(componentSymbol, context.CancellationToken))
        {
            return;
        }

        foreach (var method in localMethodAnalyses.Keys
                     .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                     .OrderBy(static method => method.GetPreferredSourceLocation().TryGetStartLine() ?? int.MaxValue))
        {
            if (!ShouldReportLifecycleMissingTryCatch(method, localMethodAnalyses))
            {
                continue;
            }

            var methodLocation = method.GetPreferredSourceLocation();
            if (methodLocation is null)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.LifecycleMissingTryCatch,
                methodLocation,
                method.Name,
                componentSymbol.Name,
                GetLifecycleRiskLabel(method)));
        }
    }

    private static void ReportLocalDisposeTryCatchDiagnostics(
        SymbolAnalysisContext context,
        INamedTypeSymbol componentSymbol,
        ConcurrentDictionary<IMethodSymbol, MethodAnalysis> localMethodAnalyses)
    {
        foreach (var method in localMethodAnalyses.Keys
                     .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                     .OrderBy(static method => method.GetPreferredSourceLocation().TryGetStartLine() ?? int.MaxValue))
        {
            if (!ShouldReportDisposeMissingTryCatch(method, localMethodAnalyses))
            {
                continue;
            }

            var methodLocation = method.GetPreferredSourceLocation();
            if (methodLocation is null)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.DisposeMissingTryCatch,
                methodLocation,
                method.Name,
                componentSymbol.Name));
        }
    }

    private static void ReportLocalLifecycleJsInteropGuardDiagnostics(
        SymbolAnalysisContext context,
        INamedTypeSymbol componentSymbol,
        ConcurrentDictionary<IMethodSymbol, MethodAnalysis> localMethodAnalyses)
    {
        foreach (var method in localMethodAnalyses.Keys
                     .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                     .OrderBy(static method => method.GetPreferredSourceLocation().TryGetStartLine() ?? int.MaxValue))
        {
            if (!ShouldReportLifecycleJsInteropGuard(method, localMethodAnalyses))
            {
                continue;
            }

            var methodLocation = method.GetPreferredSourceLocation();
            if (methodLocation is null)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.JsInteropRequiresInteractivityGuard,
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
        ConcurrentDictionary<INamedTypeSymbol, ComponentRenderAnalysis> componentRenderAnalyses,
        ConcurrentDictionary<INamedTypeSymbol, byte> globalInteractiveRoutesOwners,
        ConcurrentDictionary<INamedTypeSymbol, byte> locallyReportedMissingErrorBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> locallyReportedBoundaryMissingErrorContentComponents,
        ConcurrentDictionary<IMethodSymbol, byte> locallyReportedMissingTryCatchMethods)
    {
        var effectiveRenderModes = ComputeEffectiveRenderModes(allComponents.Keys, declaredRenderModes, componentOwners);
        var relevantComponents = ComputeRelevantBoundaryComponents(effectiveRenderModes, componentOwners, localRelevantBoundaryComponents);
        var boundaryProtectedRenderModes = ComputeBoundaryProtectedRenderModes(relevantComponents, effectiveRenderModes, localBoundaryComponents, componentOwners);
        var boundaryProtectedComponents = ComputeBoundaryProtectedComponents(relevantComponents, effectiveRenderModes, boundaryProtectedRenderModes);
        var suggestedBoundaryResolvers = ComputeSuggestedBoundaryResolvers(relevantComponents, effectiveRenderModes, boundaryProtectedRenderModes, componentOwners);
        var hasGlobalInteractiveRoutes = globalInteractiveRoutesOwners.Count > 0;

        foreach (var component in relevantComponents.OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            var boundaryProtected = boundaryProtectedComponents.Contains(component);
            var renderAnalysis = componentRenderAnalyses.TryGetValue(component, out var knownRenderAnalysis)
                ? knownRenderAnalysis
                : new ComponentRenderAnalysis([], component.GetPreferredSourceLocation());
            var boundaryLocation = component.PreferNonGeneratedSourceLocation(renderAnalysis.BoundaryLocation);

            if (!boundaryProtected &&
                ShouldReportMissingErrorBoundary(component, layoutComponents, declaredRenderModes, effectiveRenderModes, boundaryProtectedRenderModes, componentOwners) &&
                !locallyReportedMissingErrorBoundaryComponents.ContainsKey(component))
            {
                var resolverSymbols = suggestedBoundaryResolvers.TryGetValue(component, out var resolvers) && resolvers.Length > 0
                    ? resolvers
                    : [component];

                foreach (var uncoveredRegion in renderAnalysis.UncoveredRegions)
                {
                    var regionLocation = component.PreferNonGeneratedSourceLocation(uncoveredRegion.DiagnosticLocation) ??
                        component.GetPreferredSourceLocation();
                    if (regionLocation is null)
                    {
                        continue;
                    }

                    foreach (var resolverSymbol in resolverSymbols)
                    {
                        var regionName = string.IsNullOrWhiteSpace(uncoveredRegion.RootName)
                            ? resolverSymbol.Name
                            : uncoveredRegion.RootName;

                        context.ReportDiagnostic(Diagnostic.Create(
                            DiagnosticDescriptors.MissingErrorBoundary,
                            regionLocation,
                            component.Name,
                            regionName,
                            resolverSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty)));
                    }
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
                .Where(buildRenderTreeRootMethods.ContainsKey)
                .Distinct<IMethodSymbol>(SymbolEqualityComparer.Default)
                .ToArray();

            var methodsWithSpecificTryCatchDiagnostics = methods
                .Where(method => HasSpecificTryCatchDiagnostic(methodAnalyses[method]))
                .ToImmutableHashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

            if (!boundaryProtected)
            {
                var unsafeMethods = FindUnsafeRootMethods(rootMethods, methodAnalyses);
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

        if (!hasGlobalInteractiveRoutes)
        {
            foreach (var layoutComponent in layoutComponents.Keys
                         .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                         .OrderBy(static component => component.ToDisplayString(), StringComparer.Ordinal))
            {
                if (!rootBoundaryComponents.ContainsKey(layoutComponent))
                {
                    continue;
                }

                var renderAnalysis = componentRenderAnalyses.TryGetValue(layoutComponent, out var knownRenderAnalysis)
                    ? knownRenderAnalysis
                    : new ComponentRenderAnalysis([], layoutComponent.GetPreferredSourceLocation());
                var boundaryLocation = layoutComponent.PreferNonGeneratedSourceLocation(renderAnalysis.BoundaryLocation) ??
                    layoutComponent.GetPreferredSourceLocation();
                if (boundaryLocation is null)
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.LayoutBoundaryShouldBeRouteKeyed,
                    boundaryLocation,
                    layoutComponent.Name));
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

    private static HashSet<INamedTypeSymbol> ComputeLifecycleBoundaryProtectedComponents(
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> protectedRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> boundaryProtectedComponentOwners)
    {
        var allComponentSet = new HashSet<INamedTypeSymbol>(effectiveRenderModes.Keys, SymbolEqualityComparer.Default);
        var protectedComponents = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var component in allComponentSet)
        {
            if (!componentOwners.TryGetValue(component, out var owners) || owners.IsEmpty)
            {
                continue;
            }

            var actionableOwners = GetConcreteComponents(owners.Keys, allComponentSet).ToArray();
            if (actionableOwners.Length == 0)
            {
                continue;
            }

            var allRenderModesCovered = true;
            boundaryProtectedComponentOwners.TryGetValue(component, out var boundaryProtectedOwners);
            foreach (var renderMode in effectiveRenderModes[component])
            {
                var renderModeCovered = true;
                foreach (var owner in actionableOwners)
                {
                    if (!effectiveRenderModes.TryGetValue(owner, out var ownerRenderModes) ||
                        !ownerRenderModes.Contains(renderMode) ||
                        !(boundaryProtectedOwners?.ContainsKey(owner) == true ||
                          (protectedRenderModes.TryGetValue(owner, out var ownerProtectedRenderModes) &&
                           ownerProtectedRenderModes.Contains(renderMode))))
                    {
                        renderModeCovered = false;
                        break;
                    }
                }

                if (!renderModeCovered)
                {
                    allRenderModesCovered = false;
                    break;
                }
            }

            if (allRenderModesCovered)
            {
                protectedComponents.Add(component);
            }
        }

        var changed = true;
        while (changed)
        {
            changed = false;

            foreach (var component in allComponentSet.Where(static component => component.IsAbstract))
            {
                if (protectedComponents.Contains(component))
                {
                    continue;
                }

                var concreteDerivedComponents = allComponentSet
                    .Where(candidate =>
                        !candidate.IsAbstract &&
                        !SymbolEqualityComparer.Default.Equals(candidate, component) &&
                        candidate.InheritsFromOrEquals(component))
                    .ToArray();

                if (concreteDerivedComponents.Length == 0)
                {
                    continue;
                }

                if (concreteDerivedComponents.All(protectedComponents.Contains))
                {
                    changed |= protectedComponents.Add(component);
                }
            }
        }

        return protectedComponents;
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
        ConcurrentDictionary<INamedTypeSymbol, byte> layoutComponents,
        IReadOnlyDictionary<INamedTypeSymbol, string?> declaredRenderModes,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> protectedRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners)
    {
        if (component.IsAbstract || layoutComponents.ContainsKey(component))
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

    private static ImmutableHashSet<string> GetBoundaryComponentNames(Compilation compilation, INamedTypeSymbol errorBoundarySymbol)
    {
        var builder = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        AddBoundaryComponentNames(compilation.Assembly.GlobalNamespace, errorBoundarySymbol, builder);
        foreach (var referencedAssembly in compilation.SourceModule.ReferencedAssemblySymbols)
        {
            AddBoundaryComponentNames(referencedAssembly.GlobalNamespace, errorBoundarySymbol, builder);
        }

        return builder.ToImmutable();
    }

    private static void AddBoundaryComponentNames(
        INamespaceSymbol namespaceSymbol,
        INamedTypeSymbol errorBoundarySymbol,
        ImmutableHashSet<string>.Builder builder)
    {
        foreach (var member in namespaceSymbol.GetMembers())
        {
            if (member is INamespaceSymbol nestedNamespace)
            {
                AddBoundaryComponentNames(nestedNamespace, errorBoundarySymbol, builder);
                continue;
            }

            if (member is INamedTypeSymbol namedType)
            {
                AddBoundaryComponentNames(namedType, errorBoundarySymbol, builder);
            }
        }
    }

    private static void AddBoundaryComponentNames(
        INamedTypeSymbol namedType,
        INamedTypeSymbol errorBoundarySymbol,
        ImmutableHashSet<string>.Builder builder)
    {
        if (namedType.TypeKind == TypeKind.Class && namedType.InheritsFromOrEquals(errorBoundarySymbol))
        {
            builder.Add(namedType.Name);
        }

        foreach (var nestedType in namedType.GetTypeMembers())
        {
            AddBoundaryComponentNames(nestedType, errorBoundarySymbol, builder);
        }
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
                generatedAnalysis.UncoveredRegions,
                generatedAnalysis.BoundaryRootLocation ?? defaultLocation);
        }

        var generatedHasTrustedBoundaryRoot =
            generatedAnalysis.HasBoundaryRoot &&
            generatedAnalysis.UncoveredRegions.Length == 0;

        if (generatedHasTrustedBoundaryRoot)
        {
            return new CombinedRootAnalysis(
                hasBoundaryRoot: true,
                boundaryRootHasErrorContent: generatedAnalysis.BoundaryRootHasErrorContent,
                rootBoundaryIsKeyed: generatedAnalysis.RootBoundaryIsKeyed,
                rootBoundaryUsesStaleRouteKey: generatedAnalysis.RootBoundaryUsesStaleRouteKey,
                uncoveredRegions: [],
                boundaryRootLocation: razorAnalysis.BoundaryRootLocation ??
                    generatedAnalysis.BoundaryRootLocation ??
                    defaultLocation);
        }

        var uncoveredRegions = MapUncoveredRegionsToRazorLocations(generatedAnalysis.UncoveredRegions, razorAnalysis);
        var hasTrustedRazorBoundaryRoot = uncoveredRegions.Length == 0 && razorAnalysis.HasBoundaryRoot;

        return new CombinedRootAnalysis(
            hasBoundaryRoot: generatedAnalysis.HasBoundaryRoot || hasTrustedRazorBoundaryRoot,
            boundaryRootHasErrorContent: (!generatedAnalysis.HasBoundaryRoot || generatedAnalysis.BoundaryRootHasErrorContent) &&
                (!hasTrustedRazorBoundaryRoot || razorAnalysis.BoundaryRootHasErrorContent),
            rootBoundaryIsKeyed: (!generatedAnalysis.HasBoundaryRoot || generatedAnalysis.RootBoundaryIsKeyed) &&
                (!hasTrustedRazorBoundaryRoot || razorAnalysis.BoundaryRootIsKeyed),
            rootBoundaryUsesStaleRouteKey: generatedAnalysis.RootBoundaryUsesStaleRouteKey,
            uncoveredRegions: uncoveredRegions,
            boundaryRootLocation: razorAnalysis.BoundaryRootLocation ??
                generatedAnalysis.BoundaryRootLocation ??
                defaultLocation);
    }

    private static ImmutableArray<InteractiveRenderRegion> AttachSourceComponent(
        ImmutableArray<InteractiveRenderRegion> uncoveredRegions,
        INamedTypeSymbol sourceComponent)
    {
        if (uncoveredRegions.IsDefaultOrEmpty)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<InteractiveRenderRegion>(uncoveredRegions.Length);
        foreach (var uncoveredRegion in uncoveredRegions)
        {
            builder.Add(uncoveredRegion.WithSourceComponent(sourceComponent));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<InteractiveRenderRegion> MapUncoveredRegionsToRazorLocations(
        ImmutableArray<InteractiveRenderRegion> uncoveredRegions,
        RazorMarkupAnalysis razorAnalysis)
    {
        if (uncoveredRegions.IsDefaultOrEmpty)
        {
            return [];
        }

        var remainingHtmlRegions = new List<RazorMarkupRegion>(razorAnalysis.HtmlInteractiveRegions);
        var remainingComponentRoots = new List<RazorComponentRoot>(razorAnalysis.ComponentRoots);
        var builder = ImmutableArray.CreateBuilder<InteractiveRenderRegion>(uncoveredRegions.Length);

        foreach (var uncoveredRegion in uncoveredRegions)
        {
            var mappedLocation = uncoveredRegion.Kind == InteractiveRenderRegionKind.HtmlEventHandler
                ? TryConsumeHtmlRegionLocation(uncoveredRegion, remainingHtmlRegions)
                : TryConsumeComponentRootLocation(uncoveredRegion, remainingComponentRoots);

            builder.Add(mappedLocation is null
                ? uncoveredRegion
                : uncoveredRegion.WithDiagnosticLocation(mappedLocation));
        }

        return builder.ToImmutable();
    }

    private static Location? TryConsumeHtmlRegionLocation(
        InteractiveRenderRegion uncoveredRegion,
        List<RazorMarkupRegion> remainingHtmlRegions)
    {
        var matchIndex = remainingHtmlRegions.FindIndex(candidate =>
            string.Equals(GetSimpleTagName(candidate.TagName), uncoveredRegion.RootName, StringComparison.OrdinalIgnoreCase));
        if (matchIndex < 0 && remainingHtmlRegions.Count > 0)
        {
            matchIndex = 0;
        }

        if (matchIndex < 0)
        {
            return null;
        }

        var match = remainingHtmlRegions[matchIndex];
        remainingHtmlRegions.RemoveAt(matchIndex);
        return match.DiagnosticLocation;
    }

    private static Location? TryConsumeComponentRootLocation(
        InteractiveRenderRegion uncoveredRegion,
        List<RazorComponentRoot> remainingComponentRoots)
    {
        var matchIndex = remainingComponentRoots.FindIndex(candidate =>
            string.Equals(GetSimpleTagName(candidate.TagName), uncoveredRegion.RootName, StringComparison.OrdinalIgnoreCase));
        if (matchIndex < 0 && remainingComponentRoots.Count > 0)
        {
            matchIndex = 0;
        }

        if (matchIndex < 0)
        {
            return null;
        }

        var match = remainingComponentRoots[matchIndex];
        remainingComponentRoots.RemoveAt(matchIndex);
        return uncoveredRegion.Kind == InteractiveRenderRegionKind.ComponentBinding && match.BindingLocation is not null
            ? match.BindingLocation
            : match.RootLocation;
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

        var unguardedJsInteropCallees = ImmutableHashSet.CreateBuilder<IMethodSymbol>(SymbolEqualityComparer.Default);
        var unhandledFailureProneCallees = ImmutableHashSet.CreateBuilder<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var invocation in GetLocalMemberInvocations(methodDeclaration, semanticModel, methodSymbol.ContainingType, cancellationToken))
        {
            if (IsWithinInteractivityGuard(invocation, semanticModel, methodSymbol.ContainingType, cancellationToken))
            {
                continue;
            }

            var callee = GetInvokedMemberMethod(invocation, semanticModel, methodSymbol.ContainingType, cancellationToken);
            if (callee is not null)
            {
                unguardedJsInteropCallees.Add(callee);
            }
        }

        foreach (var invocation in GetLocalMemberInvocations(methodDeclaration, semanticModel, methodSymbol.ContainingType, cancellationToken))
        {
            if (IsFailureProneOperationMeaningfullyHandled(invocation, semanticModel, cancellationToken))
            {
                continue;
            }

            var callee = GetInvokedMemberMethod(invocation, semanticModel, methodSymbol.ContainingType, cancellationToken);
            if (callee is not null)
            {
                unhandledFailureProneCallees.Add(callee);
            }
        }

        var jsInteropCalls = GetJsInteropInvocations(methodDeclaration, semanticModel, cancellationToken).ToArray();
        var unhandledJsInteropCalls = GetUnhandledJsInteropInvocations(
            methodDeclaration,
            semanticModel,
            methodSymbol,
            cancellationToken);

        return new MethodAnalysis(
            hasTryCatch: MethodContainsTryCatch(methodDeclaration),
            callees: callees.ToImmutable(),
            delegatedMethod: GetDelegatedMethod(methodDeclaration, semanticModel, methodSymbol.ContainingType, cancellationToken),
            isLifecycleMethod: IsLifecycleMethod(methodSymbol),
            isDisposeMethod: IsDisposeMethod(methodSymbol),
            hasOperationalCode: HasOperationalCode(methodDeclaration),
            hasFailureProneOperation: HasFailureProneOperation(
                methodDeclaration,
                semanticModel,
                methodSymbol.ContainingType,
                treatExplicitThrowsAsFailureProne: !IsLifecycleMethod(methodSymbol),
                cancellationToken),
            hasUnhandledFailureProneOperation: HasUnhandledFailureProneOperation(
                methodDeclaration,
                semanticModel,
                methodSymbol.ContainingType,
                treatExplicitThrowsAsFailureProne: !IsLifecycleMethod(methodSymbol),
                cancellationToken),
            hasJsInteropCalls: jsInteropCalls.Length > 0,
            hasUnhandledJsInteropCalls: unhandledJsInteropCalls.Length > 0,
            hasUnguardedJsInteropCalls: jsInteropCalls.Any(call => !IsWithinInteractivityGuard(call, semanticModel, methodSymbol.ContainingType, cancellationToken)),
            unhandledFailureProneCallees: unhandledFailureProneCallees.ToImmutable(),
            unguardedJsInteropCallees: unguardedJsInteropCallees.ToImmutable(),
            isAsyncVoid: IsAsyncVoid(methodDeclaration, methodSymbol),
            catchWithoutLoggingLocations: GetCatchWithoutLoggingLocations(methodDeclaration, semanticModel, cancellationToken));
    }

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
        ((!analysis.HasTryCatch && analysis.IsLifecycleMethod && analysis.HasFailureProneOperation) ||
         (!analysis.HasTryCatch && analysis.IsDisposeMethod && analysis.HasFailureProneOperation) ||
         analysis.HasUnhandledJsInteropCalls);

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

    private static bool HasFailureProneOperation(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        bool treatExplicitThrowsAsFailureProne,
        CancellationToken cancellationToken)
    {
        var rootNode = GetMethodExecutableRoot(methodDeclaration);
        if (rootNode is null)
        {
            return false;
        }

        foreach (var node in rootNode.DescendantNodesAndSelf(static node => !IsNestedFunctionLike(node)))
        {
            switch (node)
            {
                case ThrowStatementSyntax when treatExplicitThrowsAsFailureProne:
                case ThrowExpressionSyntax when treatExplicitThrowsAsFailureProne:
                    return true;

                case AwaitExpressionSyntax awaitExpression
                    when IsFailureProneAwaitExpression(awaitExpression, semanticModel, containingType, cancellationToken):
                    return true;

                case InvocationExpressionSyntax invocation
                    when IsFailureProneInvocation(invocation, semanticModel, containingType, cancellationToken, isAwaited: false):
                    return true;
            }
        }

        return false;
    }

    private static bool HasUnhandledFailureProneOperation(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        bool treatExplicitThrowsAsFailureProne,
        CancellationToken cancellationToken)
    {
        var rootNode = GetMethodExecutableRoot(methodDeclaration);
        if (rootNode is null)
        {
            return false;
        }

        foreach (var node in rootNode.DescendantNodesAndSelf(static node => !IsNestedFunctionLike(node)))
        {
            switch (node)
            {
                case ThrowStatementSyntax when treatExplicitThrowsAsFailureProne && !IsFailureProneOperationMeaningfullyHandled(node, semanticModel, cancellationToken):
                case ThrowExpressionSyntax when treatExplicitThrowsAsFailureProne && !IsFailureProneOperationMeaningfullyHandled(node, semanticModel, cancellationToken):
                    return true;

                case AwaitExpressionSyntax awaitExpression
                    when IsFailureProneAwaitExpression(awaitExpression, semanticModel, containingType, cancellationToken) &&
                         !IsFailureProneOperationMeaningfullyHandled(awaitExpression, semanticModel, cancellationToken):
                    return true;

                case InvocationExpressionSyntax invocation
                    when invocation.Ancestors().OfType<AwaitExpressionSyntax>().FirstOrDefault() is null &&
                         IsFailureProneInvocation(invocation, semanticModel, containingType, cancellationToken, isAwaited: false) &&
                         !IsFailureProneOperationMeaningfullyHandled(invocation, semanticModel, cancellationToken):
                    return true;
            }
        }

        return false;
    }

    private static bool IsFailureProneAwaitExpression(
        AwaitExpressionSyntax awaitExpression,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        var expression = awaitExpression.Expression;
        while (expression is ParenthesizedExpressionSyntax parenthesizedExpression)
        {
            expression = parenthesizedExpression.Expression;
        }

        if (expression is InvocationExpressionSyntax invocation)
        {
            return IsFailureProneInvocation(invocation, semanticModel, containingType, cancellationToken, isAwaited: true);
        }

        return !IsKnownCompletedTaskExpression(expression, semanticModel, cancellationToken);
    }

    private static bool IsFailureProneInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken,
        bool isAwaited)
    {
        if (GetInvokedMemberMethod(invocation, semanticModel, containingType, cancellationToken) is not null)
        {
            return false;
        }

        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            IsKnownJsInteropInvocation(memberAccess, semanticModel, cancellationToken))
        {
            return true;
        }

        if (TryGetSymbol(invocation, semanticModel, cancellationToken) is not IMethodSymbol methodSymbol)
        {
            return isAwaited;
        }

        methodSymbol = NormalizeMethodSymbol(methodSymbol);
        if (IsJsInteropMethod(methodSymbol) || IsKnownLowRiskInvocation(invocation, methodSymbol))
        {
            return IsJsInteropMethod(methodSymbol);
        }

        return isAwaited || IsOperationalExternalInvocation(invocation, methodSymbol, semanticModel, cancellationToken);
    }

    private static bool IsKnownCompletedTaskExpression(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (TryGetSymbol(expression, semanticModel, cancellationToken) is not IPropertySymbol propertySymbol)
        {
            return false;
        }

        return propertySymbol.Name is "CompletedTask" &&
               GetTypeMetadataNames(propertySymbol.ContainingType).Any(static name => name == "System.Threading.Tasks.Task");
    }

    private static bool IsKnownLowRiskInvocation(InvocationExpressionSyntax invocation, IMethodSymbol methodSymbol)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax { Expression: BaseExpressionSyntax } &&
            methodSymbol.Name is "OnInitialized" or "OnInitializedAsync" or "OnParametersSet" or "OnParametersSetAsync" or "OnAfterRender" or "OnAfterRenderAsync" or "SetParametersAsync")
        {
            return true;
        }

        var containingTypeNames = GetTypeMetadataNames(methodSymbol.ContainingType).ToArray();
        if (containingTypeNames.Any(static name =>
                name is "Microsoft.AspNetCore.Components.ComponentBase" or
                    "System.Threading.Tasks.Task" or
                    "System.Threading.Tasks.ValueTask" or
                    "System.Nullable" or
                    "System.Type" or
                    "System.String" or
                    "System.Guid" ||
                name.StartsWith("System.Linq.", StringComparison.Ordinal) ||
                name.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
                name.StartsWith("System.Collections.", StringComparison.Ordinal)))
        {
            return true;
        }

        return methodSymbol.ContainingNamespace?.ToDisplayString() is { } containingNamespace &&
               (containingNamespace.StartsWith("System.Linq", StringComparison.Ordinal) ||
                containingNamespace.StartsWith("System.Reflection", StringComparison.Ordinal) ||
                containingNamespace.StartsWith("Microsoft.Extensions.DependencyInjection", StringComparison.Ordinal));
    }

    private static bool IsOperationalExternalInvocation(
        InvocationExpressionSyntax invocation,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (!IsOperationalMethodName(methodSymbol.Name))
        {
            return false;
        }

        if (TryGetInvocationReceiverType(invocation, methodSymbol, semanticModel, cancellationToken) is not INamedTypeSymbol receiverType ||
            IsKnownLowRiskReceiverType(receiverType))
        {
            return false;
        }

        return GetTypeMetadataNames(receiverType).Any(static name =>
            name.EndsWith("Repository", StringComparison.Ordinal) ||
            name.EndsWith("Service", StringComparison.Ordinal) ||
            name.EndsWith("Client", StringComparison.Ordinal) ||
            name.EndsWith("Provider", StringComparison.Ordinal) ||
            name.EndsWith("Manager", StringComparison.Ordinal) ||
            name.EndsWith("Store", StringComparison.Ordinal) ||
            name.EndsWith("Context", StringComparison.Ordinal) ||
            name.EndsWith("DbContext", StringComparison.Ordinal));
    }

    private static bool IsKnownLowRiskReceiverType(INamedTypeSymbol receiverType) =>
        GetTypeMetadataNames(receiverType).Any(static name =>
            name is "Microsoft.AspNetCore.Components.NavigationManager" or
                "System.IServiceProvider");

    private static bool IsOperationalMethodName(string methodName) =>
        methodName.StartsWith("Get", StringComparison.Ordinal) ||
        methodName.StartsWith("Load", StringComparison.Ordinal) ||
        methodName.StartsWith("Save", StringComparison.Ordinal) ||
        methodName.StartsWith("Search", StringComparison.Ordinal) ||
        methodName.StartsWith("Export", StringComparison.Ordinal) ||
        methodName.StartsWith("Import", StringComparison.Ordinal) ||
        methodName.StartsWith("Open", StringComparison.Ordinal) ||
        methodName.StartsWith("Download", StringComparison.Ordinal) ||
        methodName.StartsWith("Upload", StringComparison.Ordinal) ||
        methodName.StartsWith("Refresh", StringComparison.Ordinal) ||
        methodName.StartsWith("Invoke", StringComparison.Ordinal) ||
        methodName.StartsWith("Send", StringComparison.Ordinal) ||
        methodName.StartsWith("Submit", StringComparison.Ordinal) ||
        methodName.StartsWith("Create", StringComparison.Ordinal) ||
        methodName.StartsWith("Update", StringComparison.Ordinal) ||
        methodName.StartsWith("Patch", StringComparison.Ordinal) ||
        methodName.StartsWith("Delete", StringComparison.Ordinal) ||
        methodName.StartsWith("Remove", StringComparison.Ordinal) ||
        methodName.StartsWith("Archive", StringComparison.Ordinal) ||
        methodName.StartsWith("Restore", StringComparison.Ordinal);

    private static ITypeSymbol? TryGetInvocationReceiverType(
        InvocationExpressionSyntax invocation,
        IMethodSymbol methodSymbol,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            return GetValueExpressionType(memberAccess.Expression, semanticModel, cancellationToken);
        }

        if (methodSymbol.IsExtensionMethod && methodSymbol.Parameters.Length > 0)
        {
            return methodSymbol.Parameters[0].Type;
        }

        return null;
    }

    private static ImmutableHashSet<IMethodSymbol> FindUnsafeRootMethods(
        IEnumerable<IMethodSymbol> rootMethods,
        IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        var unsafeMethods = ImmutableHashSet.CreateBuilder<IMethodSymbol>(SymbolEqualityComparer.Default);

        foreach (var rootMethod in rootMethods)
        {
            if (IsEffectivelyHandledByDelegation(rootMethod, methodAnalyses) ||
                !HasFailurePronePath(rootMethod, methodAnalyses))
            {
                continue;
            }

            unsafeMethods.Add(rootMethod);
        }

        return unsafeMethods.ToImmutable();
    }

    private static bool ShouldReportLifecycleMissingTryCatch(
        IMethodSymbol method,
        IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        if (!methodAnalyses.TryGetValue(method, out var analysis) ||
            !analysis.IsLifecycleMethod ||
            !HasFailurePronePath(method, methodAnalyses))
        {
            return false;
        }

        return !HasMeaningfulLifecycleHandling(method, methodAnalyses);
    }

    private static bool ShouldReportDisposeMissingTryCatch(
        IMethodSymbol method,
        IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        if (!methodAnalyses.TryGetValue(method, out var analysis) ||
            !analysis.IsDisposeMethod ||
            !HasFailurePronePath(method, methodAnalyses))
        {
            return false;
        }

        return !HasMeaningfulDisposeHandling(method, methodAnalyses);
    }

    private static bool ShouldReportLifecycleJsInteropGuard(
        IMethodSymbol method,
        IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        if (!methodAnalyses.TryGetValue(method, out var analysis) ||
            !analysis.IsLifecycleMethod ||
            !IsPreRenderLifecycleMethod(method))
        {
            return false;
        }

        return HasUnguardedJsInteropPath(method, methodAnalyses);
    }

    private static bool HasMeaningfulLifecycleHandling(
        IMethodSymbol method,
        IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        var effectiveHandlingCache = new Dictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);
        var visitingEffectiveHandling = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        return IsEffectivelyHandled(method);

        bool IsEffectivelyHandled(IMethodSymbol currentMethod)
        {
            if (effectiveHandlingCache.TryGetValue(currentMethod, out var cached))
            {
                return cached;
            }

            if (!methodAnalyses.TryGetValue(currentMethod, out var analysis))
            {
                return false;
            }

            if (analysis.HasUnhandledFailureProneOperation)
            {
                effectiveHandlingCache[currentMethod] = false;
                return false;
            }

            if (!visitingEffectiveHandling.Add(currentMethod))
            {
                effectiveHandlingCache[currentMethod] = false;
                return false;
            }

            try
            {
                foreach (var callee in analysis.UnhandledFailureProneCallees)
                {
                    if (!HasFailurePronePath(callee, methodAnalyses))
                    {
                        continue;
                    }

                    if (!IsEffectivelyHandled(callee))
                    {
                        effectiveHandlingCache[currentMethod] = false;
                        return false;
                    }
                }

                effectiveHandlingCache[currentMethod] = true;
                return true;
            }
            finally
            {
                visitingEffectiveHandling.Remove(currentMethod);
            }
        }
    }

    private static bool HasMeaningfulDisposeHandling(
        IMethodSymbol method,
        IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        var effectiveHandlingCache = new Dictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);
        var visitingEffectiveHandling = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        return IsEffectivelyHandled(method);

        bool IsEffectivelyHandled(IMethodSymbol currentMethod)
        {
            if (effectiveHandlingCache.TryGetValue(currentMethod, out var cached))
            {
                return cached;
            }

            if (!methodAnalyses.TryGetValue(currentMethod, out var analysis))
            {
                return false;
            }

            if (analysis.HasUnhandledFailureProneOperation)
            {
                effectiveHandlingCache[currentMethod] = false;
                return false;
            }

            if (!visitingEffectiveHandling.Add(currentMethod))
            {
                effectiveHandlingCache[currentMethod] = false;
                return false;
            }

            try
            {
                foreach (var callee in analysis.UnhandledFailureProneCallees)
                {
                    if (!HasFailurePronePath(callee, methodAnalyses))
                    {
                        continue;
                    }

                    if (!IsEffectivelyHandled(callee))
                    {
                        effectiveHandlingCache[currentMethod] = false;
                        return false;
                    }
                }

                effectiveHandlingCache[currentMethod] = true;
                return true;
            }
            finally
            {
                visitingEffectiveHandling.Remove(currentMethod);
            }
        }
    }

    private static bool IsEffectivelyHandledByDelegation(
        IMethodSymbol method,
        IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        var effectiveHandlingCache = new Dictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);
        var visitingEffectiveHandling = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        return IsEffectivelyHandled(method);

        bool IsEffectivelyHandled(IMethodSymbol currentMethod)
        {
            if (effectiveHandlingCache.TryGetValue(currentMethod, out var cached))
            {
                return cached;
            }

            if (!methodAnalyses.TryGetValue(currentMethod, out var analysis))
            {
                return false;
            }

            if (analysis.HasTryCatch)
            {
                effectiveHandlingCache[currentMethod] = true;
                return true;
            }

            if (analysis.DelegatedMethod is null || !visitingEffectiveHandling.Add(currentMethod))
            {
                effectiveHandlingCache[currentMethod] = false;
                return false;
            }

            try
            {
                var handled = IsEffectivelyHandled(analysis.DelegatedMethod);
                effectiveHandlingCache[currentMethod] = handled;
                return handled;
            }
            finally
            {
                visitingEffectiveHandling.Remove(currentMethod);
            }
        }
    }

    private static bool HasFailurePronePath(
        IMethodSymbol method,
        IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        var failureProneCache = new Dictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);
        var visitingFailureProne = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        return HasFailurePronePathCore(method);

        bool HasFailurePronePathCore(IMethodSymbol currentMethod)
        {
            if (failureProneCache.TryGetValue(currentMethod, out var cached))
            {
                return cached;
            }

            if (!methodAnalyses.TryGetValue(currentMethod, out var analysis))
            {
                return false;
            }

            if (analysis.HasFailureProneOperation)
            {
                failureProneCache[currentMethod] = true;
                return true;
            }

            if (!visitingFailureProne.Add(currentMethod))
            {
                failureProneCache[currentMethod] = false;
                return false;
            }

            try
            {
                foreach (var callee in analysis.Callees)
                {
                    if (HasFailurePronePathCore(callee))
                    {
                        failureProneCache[currentMethod] = true;
                        return true;
                    }
                }

                if (analysis.DelegatedMethod is not null && HasFailurePronePathCore(analysis.DelegatedMethod))
                {
                    failureProneCache[currentMethod] = true;
                    return true;
                }

                failureProneCache[currentMethod] = false;
                return false;
            }
            finally
            {
                visitingFailureProne.Remove(currentMethod);
            }
        }
    }

    private static string GetLifecycleRiskLabel(IMethodSymbol methodSymbol) =>
        methodSymbol.Name switch
        {
            "OnAfterRender" or "OnAfterRenderAsync" => "after-render",
            _ => "early"
        };

    private static bool HasUnguardedJsInteropPath(
        IMethodSymbol method,
        IReadOnlyDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        var unguardedJsCache = new Dictionary<IMethodSymbol, bool>(SymbolEqualityComparer.Default);
        var visitingUnguardedJs = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        return HasUnguardedJsInteropPathCore(method);

        bool HasUnguardedJsInteropPathCore(IMethodSymbol currentMethod)
        {
            if (unguardedJsCache.TryGetValue(currentMethod, out var cached))
            {
                return cached;
            }

            if (!methodAnalyses.TryGetValue(currentMethod, out var analysis))
            {
                return false;
            }

            if (analysis.HasUnguardedJsInteropCalls)
            {
                unguardedJsCache[currentMethod] = true;
                return true;
            }

            if (!visitingUnguardedJs.Add(currentMethod))
            {
                unguardedJsCache[currentMethod] = false;
                return false;
            }

            try
            {
                foreach (var callee in analysis.UnguardedJsInteropCallees)
                {
                    if (HasUnguardedJsInteropPathCore(callee))
                    {
                        unguardedJsCache[currentMethod] = true;
                        return true;
                    }
                }

                if (analysis.DelegatedMethod is not null && HasUnguardedJsInteropPathCore(analysis.DelegatedMethod))
                {
                    unguardedJsCache[currentMethod] = true;
                    return true;
                }

                unguardedJsCache[currentMethod] = false;
                return false;
            }
            finally
            {
                visitingUnguardedJs.Remove(currentMethod);
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

        var analysis = AnalyzeBuildRenderTreeStatements(
            body.Statements,
            childComponents.ToImmutable(),
            semanticModel,
            errorBoundarySymbol,
            cancellationToken);
        if (!analysis.HasBoundaryRoot && analysis.RootBoundaryComponent is null)
        {
            return analysis;
        }

        var boundaryProtectedChildComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        boundaryProtectedChildComponents.UnionWith(analysis.BoundaryProtectedChildComponents);
        var boundaryProtectedDynamicComponentTypeParameters = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        boundaryProtectedDynamicComponentTypeParameters.UnionWith(analysis.BoundaryProtectedDynamicComponentTypeParameters);
        foreach (var dynamicComponentReference in GetBoundaryProtectedDynamicComponentReferences(body, semanticModel, cancellationToken))
        {
            if (dynamicComponentReference.ComponentType is not null)
            {
                boundaryProtectedChildComponents.Add(dynamicComponentReference.ComponentType);
                continue;
            }

            if (dynamicComponentReference.TypeParameterName is not null)
            {
                boundaryProtectedDynamicComponentTypeParameters.Add(dynamicComponentReference.TypeParameterName);
            }
        }

        return new RenderTreeAnalysis(
            analysis.HasBoundaryRoot,
            analysis.BoundaryRootHasErrorContent,
            analysis.RootBoundaryIsKeyed,
            analysis.RootBoundaryUsesStaleRouteKey,
            analysis.RootBoundaryComponent,
            analysis.ChildComponents,
            boundaryProtectedChildComponents.ToImmutable(),
            boundaryProtectedDynamicComponentTypeParameters.ToImmutable(),
            analysis.UncoveredRegions,
            analysis.BoundaryRootLocation);
    }

    private static IEnumerable<DynamicComponentReference> GetBoundaryProtectedDynamicComponentReferences(
        BlockSyntax body,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (!IsDynamicComponentTypeAttribute(invocation, semanticModel, cancellationToken) ||
                invocation.ArgumentList.Arguments.Count < 3)
            {
                continue;
            }

            var valueExpression = StripParentheses(invocation.ArgumentList.Arguments[2].Expression);
            if (TryGetTypeOfComponent(valueExpression, semanticModel, cancellationToken) is { } componentType)
            {
                yield return new DynamicComponentReference(componentType, typeParameterName: null);
                continue;
            }

            if (TryGetReferencedMemberName(valueExpression) is { } typeParameterName)
            {
                yield return new DynamicComponentReference(componentType: null, typeParameterName);
            }
        }
    }

    private static bool IsDynamicComponentTypeAttribute(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "AddAttribute" or "AddComponentParameter" } ||
            !HasAttributeNamed(invocation, semanticModel, cancellationToken, "Type") ||
            invocation.Parent is not ExpressionStatementSyntax statement ||
            statement.Parent is not BlockSyntax block)
        {
            return false;
        }

        var statementIndex = block.Statements.IndexOf(statement);
        for (var index = statementIndex - 1; index >= 0; index--)
        {
            if (block.Statements[index] is not ExpressionStatementSyntax { Expression: InvocationExpressionSyntax previousInvocation } ||
                previousInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: var invocationName })
            {
                continue;
            }

            if (invocationName == "CloseComponent")
            {
                return false;
            }

            if (invocationName == "OpenComponent")
            {
                return IsDynamicComponentType(TryGetComponentType(previousInvocation, semanticModel, cancellationToken));
            }
        }

        return false;
    }

    private static RenderTreeAnalysis AnalyzeBuildRenderTreeStatements(
        IEnumerable<StatementSyntax> statements,
        ImmutableHashSet<INamedTypeSymbol> childComponents,
        SemanticModel semanticModel,
        INamedTypeSymbol errorBoundarySymbol,
        CancellationToken cancellationToken)
    {
        var uncoveredRegions = ImmutableArray.CreateBuilder<InteractiveRenderRegion>();
        var boundaryProtectedChildComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var boundaryProtectedDynamicComponentTypeParameters = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var hasBoundaryProtectedContent = false;
        var boundaryRootHasErrorContent = true;
        var rootBoundaryIsKeyed = true;
        var rootBoundaryUsesStaleRouteKey = false;
        INamedTypeSymbol? rootBoundaryComponent = null;
        Location? boundaryRootLocation = null;
        RootAnalysisState? currentRoot = null;

        foreach (var statement in statements)
        {
            if (currentRoot is null && statement is IfStatementSyntax ifStatement)
            {
                MergeRenderTreeAnalysis(
                    AnalyzeStatementBranches(ifStatement, childComponents, semanticModel, errorBoundarySymbol, cancellationToken),
                    uncoveredRegions,
                    boundaryProtectedChildComponents,
                    boundaryProtectedDynamicComponentTypeParameters,
                    ref hasBoundaryProtectedContent,
                    ref boundaryRootHasErrorContent,
                    ref rootBoundaryIsKeyed,
                    ref rootBoundaryUsesStaleRouteKey,
                    ref rootBoundaryComponent,
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
                        currentRoot = RootAnalysisState.CreateElementRoot(
                            invocation.GetLocation(),
                            TryGetElementName(invocation, semanticModel, cancellationToken));
                    }
                    else
                    {
                        currentRoot.OpenElement(invocation.GetLocation(), TryGetElementName(invocation, semanticModel, cancellationToken));
                    }
                    break;

                case "AddAttribute":
                case "AddComponentParameter":
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
                        hasBoundaryProtectedContent |= currentRoot.HasBoundaryProtectedContent;
                        boundaryRootHasErrorContent &= !currentRoot.HasBoundaryMissingErrorContent;
                        rootBoundaryIsKeyed &= currentRoot.RootBoundaryIsKeyed;
                        rootBoundaryUsesStaleRouteKey |= currentRoot.RootBoundaryUsesStaleRouteKey;
                        rootBoundaryComponent ??= currentRoot.RootBoundaryComponent;
                        uncoveredRegions.AddRange(currentRoot.UncoveredRegions);
                        boundaryProtectedChildComponents.UnionWith(currentRoot.BoundaryProtectedChildComponents);
                        boundaryProtectedDynamicComponentTypeParameters.UnionWith(currentRoot.BoundaryProtectedDynamicComponentTypeParameters);
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
                uncoveredRegions,
                boundaryProtectedChildComponents,
                boundaryProtectedDynamicComponentTypeParameters,
                ref hasBoundaryProtectedContent,
                ref boundaryRootHasErrorContent,
                ref rootBoundaryIsKeyed,
                ref rootBoundaryUsesStaleRouteKey,
                ref rootBoundaryComponent,
                ref boundaryRootLocation);
        }

        var finalizedUncoveredRegions = uncoveredRegions.ToImmutable();
        return new RenderTreeAnalysis(
            hasBoundaryRoot: hasBoundaryProtectedContent && finalizedUncoveredRegions.Length == 0,
            boundaryRootHasErrorContent,
            rootBoundaryIsKeyed,
            rootBoundaryUsesStaleRouteKey,
            rootBoundaryComponent,
            childComponents,
            boundaryProtectedChildComponents.ToImmutable(),
            boundaryProtectedDynamicComponentTypeParameters.ToImmutable(),
            finalizedUncoveredRegions,
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

        var uncoveredRegions = ImmutableArray.CreateBuilder<InteractiveRenderRegion>();
        var boundaryProtectedChildComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var boundaryProtectedDynamicComponentTypeParameters = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        var hasBoundaryProtectedContent = false;
        var boundaryRootHasErrorContent = true;
        var rootBoundaryIsKeyed = true;
        var rootBoundaryUsesStaleRouteKey = false;
        INamedTypeSymbol? rootBoundaryComponent = null;
        Location? boundaryRootLocation = null;

        MergeRenderTreeAnalysis(
            ifAnalysis,
            uncoveredRegions,
            boundaryProtectedChildComponents,
            boundaryProtectedDynamicComponentTypeParameters,
            ref hasBoundaryProtectedContent,
            ref boundaryRootHasErrorContent,
            ref rootBoundaryIsKeyed,
            ref rootBoundaryUsesStaleRouteKey,
            ref rootBoundaryComponent,
            ref boundaryRootLocation);
        MergeRenderTreeAnalysis(
            elseAnalysis,
            uncoveredRegions,
            boundaryProtectedChildComponents,
            boundaryProtectedDynamicComponentTypeParameters,
            ref hasBoundaryProtectedContent,
            ref boundaryRootHasErrorContent,
            ref rootBoundaryIsKeyed,
            ref rootBoundaryUsesStaleRouteKey,
            ref rootBoundaryComponent,
            ref boundaryRootLocation);

        var finalizedUncoveredRegions = uncoveredRegions.ToImmutable();
        return new RenderTreeAnalysis(
            hasBoundaryRoot: hasBoundaryProtectedContent && finalizedUncoveredRegions.Length == 0,
            boundaryRootHasErrorContent,
            rootBoundaryIsKeyed,
            rootBoundaryUsesStaleRouteKey,
            rootBoundaryComponent,
            childComponents,
            boundaryProtectedChildComponents.ToImmutable(),
            boundaryProtectedDynamicComponentTypeParameters.ToImmutable(),
            finalizedUncoveredRegions,
            boundaryRootLocation);
    }

    private static IEnumerable<StatementSyntax> GetBranchStatements(StatementSyntax statement) =>
        statement is BlockSyntax block ? block.Statements : [statement];

    private static void MergeRenderTreeAnalysis(
        RenderTreeAnalysis analysis,
        ImmutableArray<InteractiveRenderRegion>.Builder uncoveredRegions,
        ImmutableHashSet<INamedTypeSymbol>.Builder boundaryProtectedChildComponents,
        ImmutableHashSet<string>.Builder boundaryProtectedDynamicComponentTypeParameters,
        ref bool hasBoundaryProtectedContent,
        ref bool boundaryRootHasErrorContent,
        ref bool rootBoundaryIsKeyed,
        ref bool rootBoundaryUsesStaleRouteKey,
        ref INamedTypeSymbol? rootBoundaryComponent,
        ref Location? boundaryRootLocation)
    {
        uncoveredRegions.AddRange(analysis.UncoveredRegions);
        boundaryProtectedChildComponents.UnionWith(analysis.BoundaryProtectedChildComponents);
        boundaryProtectedDynamicComponentTypeParameters.UnionWith(analysis.BoundaryProtectedDynamicComponentTypeParameters);
        hasBoundaryProtectedContent |= analysis.HasBoundaryRoot || analysis.RootBoundaryComponent is not null;
        boundaryRootHasErrorContent &= analysis.BoundaryRootHasErrorContent;
        if (analysis.HasBoundaryRoot || analysis.RootBoundaryComponent is not null)
        {
            rootBoundaryIsKeyed &= analysis.RootBoundaryIsKeyed;
        }
        rootBoundaryUsesStaleRouteKey |= analysis.RootBoundaryUsesStaleRouteKey;

        rootBoundaryComponent ??= analysis.RootBoundaryComponent;
        boundaryRootLocation ??= analysis.BoundaryRootLocation;
    }

    private static void MergeRootAnalysisState(
        RootAnalysisState rootAnalysisState,
        ImmutableArray<InteractiveRenderRegion>.Builder uncoveredRegions,
        ImmutableHashSet<INamedTypeSymbol>.Builder boundaryProtectedChildComponents,
        ImmutableHashSet<string>.Builder boundaryProtectedDynamicComponentTypeParameters,
        ref bool hasBoundaryProtectedContent,
        ref bool boundaryRootHasErrorContent,
        ref bool rootBoundaryIsKeyed,
        ref bool rootBoundaryUsesStaleRouteKey,
        ref INamedTypeSymbol? rootBoundaryComponent,
        ref Location? boundaryRootLocation)
    {
        uncoveredRegions.AddRange(rootAnalysisState.UncoveredRegions);
        boundaryProtectedChildComponents.UnionWith(rootAnalysisState.BoundaryProtectedChildComponents);
        boundaryProtectedDynamicComponentTypeParameters.UnionWith(rootAnalysisState.BoundaryProtectedDynamicComponentTypeParameters);
        hasBoundaryProtectedContent |= rootAnalysisState.HasBoundaryProtectedContent;
        boundaryRootHasErrorContent &= !rootAnalysisState.HasBoundaryMissingErrorContent;
        rootBoundaryIsKeyed &= rootAnalysisState.RootBoundaryIsKeyed;
        rootBoundaryUsesStaleRouteKey |= rootAnalysisState.RootBoundaryUsesStaleRouteKey;
        rootBoundaryComponent ??= rootAnalysisState.RootBoundaryComponent;
        boundaryRootLocation ??= rootAnalysisState.RootBoundaryLocation;
    }

    private static void MergeRootAnalysisState(
        RootAnalysisState rootAnalysisState,
        ImmutableArray<InteractiveRenderRegion>.Builder uncoveredRegions,
        ref bool hasBoundaryProtectedContent,
        ref bool boundaryRootHasErrorContent,
        ref bool rootBoundaryIsKeyed,
        ref bool rootBoundaryUsesStaleRouteKey,
        ref INamedTypeSymbol? rootBoundaryComponent,
        ref Location? boundaryRootLocation)
    {
        var boundaryProtectedChildComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var boundaryProtectedDynamicComponentTypeParameters = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        MergeRootAnalysisState(
            rootAnalysisState,
            uncoveredRegions,
            boundaryProtectedChildComponents,
            boundaryProtectedDynamicComponentTypeParameters,
            ref hasBoundaryProtectedContent,
            ref boundaryRootHasErrorContent,
            ref rootBoundaryIsKeyed,
            ref rootBoundaryUsesStaleRouteKey,
            ref rootBoundaryComponent,
            ref boundaryRootLocation);
    }

    private sealed class RootAnalysisState
    {
        private readonly Stack<NodeFrame> nodeStack = new();
        private readonly ImmutableArray<InteractiveRenderRegion>.Builder uncoveredRegions = ImmutableArray.CreateBuilder<InteractiveRenderRegion>();
        private readonly ImmutableHashSet<INamedTypeSymbol>.Builder boundaryProtectedChildComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        private readonly ImmutableHashSet<string>.Builder boundaryProtectedDynamicComponentTypeParameters = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
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
            Location rootLocation,
            string? rootName)
        {
            IgnoredRoot = ignoredRoot;
            BoundaryRoot = boundaryRoot;
            RootBoundaryComponent = rootBoundaryComponent;
            RootBoundaryLocation = boundaryRoot ? rootLocation : null;
            rootBoundaryHasErrorContent = rootBoundaryHasBuiltInErrorContent;
            nodeStack.Push(new NodeFrame(boundaryRoot, isComponent: true, rootLocation, rootName, rootBoundaryComponent));
            activeBoundaryCount = boundaryRoot ? 1 : 0;
            HasBoundaryProtectedContent = boundaryRoot;
        }

        public bool BoundaryRoot { get; }

        public bool IgnoredRoot { get; }

        public INamedTypeSymbol? RootBoundaryComponent { get; }

        public bool HasBoundaryProtectedContent { get; private set; }

        public bool HasBoundaryMissingErrorContent => rootBoundaryMissingErrorContent;

        public bool IsComplete => nodeStack.Count == 0;

        public ImmutableArray<InteractiveRenderRegion> UncoveredRegions => uncoveredRegions.ToImmutable();

        public ImmutableHashSet<INamedTypeSymbol> BoundaryProtectedChildComponents => boundaryProtectedChildComponents.ToImmutable();

        public ImmutableHashSet<string> BoundaryProtectedDynamicComponentTypeParameters => boundaryProtectedDynamicComponentTypeParameters.ToImmutable();

        public bool RootBoundaryIsKeyed => !BoundaryRoot || rootBoundaryIsKeyed;

        public bool RootBoundaryUsesStaleRouteKey => BoundaryRoot && rootBoundaryUsesStaleRouteKey;

        public Location? RootBoundaryLocation { get; }

        public static RootAnalysisState CreateElementRoot(Location rootLocation, string? elementName)
        {
            var state = new RootAnalysisState(
                ignoredRoot: false,
                boundaryRoot: false,
                rootBoundaryComponent: null,
                rootBoundaryHasBuiltInErrorContent: false,
                rootLocation,
                rootName: elementName);
            state.nodeStack.Clear();
            state.nodeStack.Push(new NodeFrame(isBoundary: false, isComponent: false, rootLocation, elementName, componentType: null));
            return state;
        }

        public static RootAnalysisState CreateComponentRoot(
            INamedTypeSymbol? componentType,
            INamedTypeSymbol errorBoundarySymbol,
            CancellationToken cancellationToken,
            Location rootLocation)
        {
            if (IsIgnoredRootComponent(componentType))
            {
                return new RootAnalysisState(ignoredRoot: true, boundaryRoot: false, rootBoundaryComponent: null, rootBoundaryHasBuiltInErrorContent: false, rootLocation, componentType?.Name);
            }

            if (componentType is not null && componentType.InheritsFromOrEquals(errorBoundarySymbol))
            {
                var hasBuiltInErrorContent = BoundaryTypeHasBuiltInErrorContent(componentType, errorBoundarySymbol, cancellationToken);
                return new RootAnalysisState(ignoredRoot: false, boundaryRoot: true, rootBoundaryComponent: componentType, rootBoundaryHasBuiltInErrorContent: hasBuiltInErrorContent, rootLocation, componentType.Name);
            }

            return new RootAnalysisState(ignoredRoot: false, boundaryRoot: false, rootBoundaryComponent: null, rootBoundaryHasBuiltInErrorContent: false, rootLocation, componentType?.Name);
        }

        public void OpenElement(Location location, string? elementName) =>
            nodeStack.Push(new NodeFrame(isBoundary: false, isComponent: false, rootLocation: location, rootName: elementName, componentType: null));

        public void OpenComponent(INamedTypeSymbol? componentType, INamedTypeSymbol errorBoundarySymbol, Location location)
        {
            if (IgnoredRoot || IsIgnoredRootComponent(componentType))
            {
                nodeStack.Push(new NodeFrame(isBoundary: false, isComponent: true, rootLocation: location, rootName: componentType?.Name, componentType));
                return;
            }

            var isBoundary = componentType is not null && componentType.InheritsFromOrEquals(errorBoundarySymbol);
            if (activeBoundaryCount != 0 &&
                componentType is not null &&
                !isBoundary)
            {
                boundaryProtectedChildComponents.Add(componentType);
            }

            nodeStack.Push(new NodeFrame(isBoundary, isComponent: true, rootLocation: location, rootName: componentType?.Name, componentType));
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

            if (nodeStack.Count == 0)
            {
                return;
            }

            var currentNode = nodeStack.Peek();
            if (activeBoundaryCount != 0)
            {
                TrackBoundaryProtectedDynamicComponentType(invocation, currentNode, semanticModel, cancellationToken);
                return;
            }

            if (!currentNode.IsComponent)
            {
                if (HasEventAttribute(invocation, semanticModel, cancellationToken))
                {
                    MarkUnprotectedInteractiveContent(
                        currentNode,
                        InteractiveRenderRegionKind.HtmlEventHandler,
                        invocation.GetLocation(),
                        GetInteractiveRegionRootMethods(invocation, semanticModel, cancellationToken));
                }

                return;
            }

            if (TryGetComponentInteractiveRegionKind(invocation, semanticModel, cancellationToken) is { } regionKind)
            {
                MarkUnprotectedInteractiveContent(
                    currentNode,
                    regionKind,
                    invocation.GetLocation(),
                    GetInteractiveRegionRootMethods(invocation, semanticModel, cancellationToken));
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

        private void MarkUnprotectedInteractiveContent(
            NodeFrame currentNode,
            InteractiveRenderRegionKind regionKind,
            Location location,
            ImmutableHashSet<IMethodSymbol> rootMethods)
        {
            if (currentNode.RecordedInteractiveRegionIndex >= 0)
            {
                var existingRegion = uncoveredRegions[currentNode.RecordedInteractiveRegionIndex];
                if (!rootMethods.IsEmpty)
                {
                    uncoveredRegions[currentNode.RecordedInteractiveRegionIndex] = existingRegion.WithRootMethods(existingRegion.RootMethods.Union(rootMethods));
                }

                return;
            }

            currentNode.RecordedInteractiveRegionIndex = uncoveredRegions.Count;
            uncoveredRegions.Add(new InteractiveRenderRegion(
                sourceComponent: null,
                diagnosticLocation: location,
                kind: regionKind,
                hasLocalBoundaryCoverage: false,
                rootName: currentNode.RootName ?? string.Empty,
                rootMethods: rootMethods));
        }

        private void TrackBoundaryProtectedDynamicComponentType(
            InvocationExpressionSyntax invocation,
            NodeFrame currentNode,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            if (!currentNode.IsComponent ||
                !IsDynamicComponentType(currentNode.ComponentType) ||
                !HasAttributeNamed(invocation, semanticModel, cancellationToken, "Type") ||
                invocation.ArgumentList.Arguments.Count < 3)
            {
                return;
            }

            var valueExpression = StripParentheses(invocation.ArgumentList.Arguments[2].Expression);
            if (TryGetTypeOfComponent(valueExpression, semanticModel, cancellationToken) is { } componentType)
            {
                boundaryProtectedChildComponents.Add(componentType);
                return;
            }

            if (TryGetReferencedMemberName(valueExpression) is { } memberName)
            {
                boundaryProtectedDynamicComponentTypeParameters.Add(memberName);
            }
        }

        private sealed class NodeFrame
        {
            public NodeFrame(bool isBoundary, bool isComponent, Location rootLocation, string? rootName, INamedTypeSymbol? componentType)
            {
                IsBoundary = isBoundary;
                IsComponent = isComponent;
                RootLocation = rootLocation;
                RootName = rootName;
                ComponentType = componentType;
            }

            public bool IsBoundary { get; }

            public bool IsComponent { get; }

            public Location RootLocation { get; }

            public string? RootName { get; }

            public INamedTypeSymbol? ComponentType { get; }

            public int RecordedInteractiveRegionIndex { get; set; } = -1;
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

    private static bool IsDynamicComponentType(INamedTypeSymbol? componentType) =>
        componentType is not null &&
        componentType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty) ==
        "Microsoft.AspNetCore.Components.DynamicComponent";

    private static INamedTypeSymbol? TryGetTypeOfComponent(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        expression = StripParentheses(expression);
        return expression is TypeOfExpressionSyntax typeOfExpression
            ? TryGetTypeInfo(typeOfExpression.Type, semanticModel, cancellationToken)?.Type as INamedTypeSymbol
            : null;
    }

    private static string? TryGetReferencedMemberName(ExpressionSyntax expression)
    {
        expression = StripParentheses(expression);
        return expression switch
        {
            IdentifierNameSyntax identifierName => identifierName.Identifier.ValueText,
            MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText,
            _ => null
        };
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

    private static InteractiveRenderRegionKind? TryGetComponentInteractiveRegionKind(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.Count < 3)
        {
            return null;
        }

        var valueExpression = invocation.ArgumentList.Arguments[2].Expression;
        var type = GetValueExpressionType(valueExpression, semanticModel, cancellationToken);
        if (type is INamedTypeSymbol renderFragmentType &&
            IsRenderFragmentType(renderFragmentType))
        {
            return null;
        }

        if (IsBindingGeneratedComponentCallback(invocation, valueExpression, semanticModel, cancellationToken))
        {
            return InteractiveRenderRegionKind.ComponentBinding;
        }

        if (valueExpression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax)
        {
            return InteractiveRenderRegionKind.ComponentCallback;
        }

        var valueSymbol = TryGetSymbol(valueExpression, semanticModel, cancellationToken);
        if (valueExpression is not InvocationExpressionSyntax && valueSymbol is IMethodSymbol)
        {
            return InteractiveRenderRegionKind.ComponentCallback;
        }

        if (type is not INamedTypeSymbol namedType)
        {
            return null;
        }

        if (namedType.TypeKind == TypeKind.Delegate)
        {
            return InteractiveRenderRegionKind.ComponentCallback;
        }

        var metadataName = namedType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        return metadataName is "Microsoft.AspNetCore.Components.EventCallback" or
            "Microsoft.AspNetCore.Components.EventCallback<TValue>"
            ? InteractiveRenderRegionKind.ComponentCallback
            : null;
    }

    private static bool IsBindingGeneratedComponentCallback(
        InvocationExpressionSyntax invocation,
        ExpressionSyntax valueExpression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (TryGetAttributeName(invocation, semanticModel, cancellationToken) is not { } attributeName)
        {
            return false;
        }

        if (!attributeName.EndsWith("Changed", StringComparison.Ordinal))
        {
            return false;
        }

        if (valueExpression is InvocationExpressionSyntax callbackInvocation &&
            IsCreateBinderInvocation(callbackInvocation, semanticModel, cancellationToken))
        {
            return true;
        }

        var type = GetValueExpressionType(valueExpression, semanticModel, cancellationToken);
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
            "Microsoft.AspNetCore.Components.EventCallback<TValue>";
    }

    private static string? TryGetAttributeName(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.Count < 2)
        {
            return null;
        }

        var constantValue = TryGetConstantValue(invocation.ArgumentList.Arguments[1].Expression, semanticModel, cancellationToken);
        return constantValue.HasValue && constantValue.Value is string value ? value : null;
    }

    private static string? TryGetElementName(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.Count < 2)
        {
            return null;
        }

        var constantValue = TryGetConstantValue(invocation.ArgumentList.Arguments[1].Expression, semanticModel, cancellationToken);
        return constantValue.HasValue && constantValue.Value is string value ? value : null;
    }

    private static string GetSimpleTagName(string tagName)
    {
        var separatorIndex = tagName.LastIndexOf('.');
        return separatorIndex >= 0 ? tagName.Substring(separatorIndex + 1) : tagName;
    }

    private static ITypeSymbol? GetValueExpressionType(
        ExpressionSyntax valueExpression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var typeInfo = TryGetTypeInfo(valueExpression, semanticModel, cancellationToken);
        if (typeInfo is null)
        {
            return null;
        }

        if (typeInfo.Value.Type is { } directType)
        {
            return directType;
        }

        return typeInfo.Value.ConvertedType;
    }

    private static bool IsRenderFragmentType(INamedTypeSymbol namedType)
    {
        var metadataName = namedType.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
        return metadataName is "Microsoft.AspNetCore.Components.RenderFragment" or
            "Microsoft.AspNetCore.Components.RenderFragment<TValue>";
    }

    private static bool IsCreateBinderInvocation(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
            memberAccess.Name.Identifier.ValueText == "CreateBinder")
        {
            return true;
        }

        return TryGetSymbol(invocation, semanticModel, cancellationToken) is IMethodSymbol callbackSymbol &&
            callbackSymbol.Name == "CreateBinder";
    }

    private static ImmutableHashSet<IMethodSymbol> GetInteractiveRegionRootMethods(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.Count < 3 ||
            semanticModel.GetEnclosingSymbol(invocation.SpanStart, cancellationToken)?.ContainingType is not INamedTypeSymbol containingType)
        {
            return ImmutableHashSet.Create<IMethodSymbol>(SymbolEqualityComparer.Default);
        }

        var rootMethods = ImmutableHashSet.CreateBuilder<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var expression in invocation.ArgumentList.Arguments[2].Expression.DescendantNodesAndSelf().OfType<ExpressionSyntax>())
        {
            if (TryGetSymbol(expression, semanticModel, cancellationToken) is not IMethodSymbol methodSymbol)
            {
                continue;
            }

            methodSymbol = NormalizeMethodSymbol(methodSymbol);
            if (IsRelevantMethod(methodSymbol, containingType))
            {
                rootMethods.Add(methodSymbol);
            }
        }

        return rootMethods.ToImmutable();
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

    private static bool HasInteractiveRoutesRenderMode(
        BlockSyntax body,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var componentStack = new Stack<INamedTypeSymbol?>();

        foreach (var invocation in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            {
                continue;
            }

            switch (memberAccess.Name.Identifier.ValueText)
            {
                case "OpenComponent":
                    componentStack.Push(TryGetOpenedComponentType(invocation, semanticModel, cancellationToken));
                    break;

                case "CloseComponent":
                    if (componentStack.Count > 0)
                    {
                        componentStack.Pop();
                    }

                    break;

                case "AddComponentRenderMode":
                    if (componentStack.Count > 0 &&
                        componentStack.Peek() is { } currentComponent &&
                        IsRoutesComponent(currentComponent))
                    {
                        return true;
                    }

                    break;
            }
        }

        return false;
    }

    private static bool IsRoutesComponent(INamedTypeSymbol componentType) =>
        GetTypeMetadataNames(componentType).Any(static name => name == "Microsoft.AspNetCore.Components.Routing.Routes");

    private static INamedTypeSymbol? TryGetOpenedComponentType(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (TryGetComponentType(invocation, semanticModel, cancellationToken) is { } genericComponentType)
        {
            return genericComponentType;
        }

        if (invocation.Expression is MemberAccessExpressionSyntax &&
            invocation.ArgumentList.Arguments.Count > 1 &&
            invocation.ArgumentList.Arguments[1].Expression is TypeOfExpressionSyntax typeOfExpression)
        {
            return TryGetTypeInfo(typeOfExpression.Type, semanticModel, cancellationToken)?.Type as INamedTypeSymbol;
        }

        return null;
    }

    private static IEnumerable<IMethodSymbol> GetCalledMemberMethods(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var invocation in GetLocalMemberInvocations(methodDeclaration, semanticModel, containingType, cancellationToken))
        {
            var methodSymbol = GetInvokedMemberMethod(invocation, semanticModel, containingType, cancellationToken);
            if (methodSymbol is null || !seen.Add(methodSymbol))
            {
                continue;
            }

            yield return methodSymbol;
        }
    }

    private static IEnumerable<InvocationExpressionSyntax> GetLocalMemberInvocations(
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

        foreach (var invocation in rootNode.DescendantNodesAndSelf(static node => !IsNestedFunctionLike(node)).OfType<InvocationExpressionSyntax>())
        {
            if (GetInvokedMemberMethod(invocation, semanticModel, containingType, cancellationToken) is not null)
            {
                yield return invocation;
            }
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
            if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                IsKnownJsInteropInvocation(memberAccess, semanticModel, cancellationToken))
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

    private static bool IsKnownJsInteropInvocation(
        MemberAccessExpressionSyntax memberAccess,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        var memberName = memberAccess.Name.Identifier.ValueText;
        if (memberName is not ("InvokeAsync" or "InvokeVoidAsync" or "DisposeAsync"))
        {
            return false;
        }

        var receiverType = GetValueExpressionType(memberAccess.Expression, semanticModel, cancellationToken) as INamedTypeSymbol;
        if (receiverType is null)
        {
            return false;
        }

        var metadataNames = GetTypeMetadataNames(receiverType).ToArray();
        return memberName switch
        {
            "InvokeAsync" or "InvokeVoidAsync" => metadataNames.Any(static name =>
                name is "Microsoft.JSInterop.IJSRuntime" or
                    "Microsoft.JSInterop.IJSInProcessRuntime" or
                    "Microsoft.JSInterop.IJSUnmarshalledRuntime" or
                    "Microsoft.JSInterop.IJSObjectReference" or
                    "Microsoft.JSInterop.IJSInProcessObjectReference"),
            "DisposeAsync" => metadataNames.Any(static name =>
                name is "Microsoft.JSInterop.IJSObjectReference" or
                    "Microsoft.JSInterop.IJSInProcessObjectReference"),
            _ => false
        };
    }

    private static ImmutableArray<InvocationExpressionSyntax> GetUnhandledJsInteropInvocations(
        MethodDeclarationSyntax methodDeclaration,
        SemanticModel semanticModel,
        IMethodSymbol methodSymbol,
        CancellationToken cancellationToken)
    {
        var builder = ImmutableArray.CreateBuilder<InvocationExpressionSyntax>();
        foreach (var invocation in GetJsInteropInvocations(methodDeclaration, semanticModel, cancellationToken))
        {
            if (IsJsInteropInvocationMeaningfullyHandled(invocation, semanticModel, methodSymbol, cancellationToken))
            {
                continue;
            }

            builder.Add(invocation);
        }

        return builder.ToImmutable();
    }

    private static bool IsJsInteropInvocationMeaningfullyHandled(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        IMethodSymbol methodSymbol,
        CancellationToken cancellationToken)
    {
        foreach (var tryStatement in invocation.Ancestors().OfType<TryStatementSyntax>())
        {
            if (!tryStatement.Block.FullSpan.Contains(invocation.Span) || tryStatement.Catches.Count == 0)
            {
                continue;
            }

            if (tryStatement.Catches.Any(catchClause => CatchClauseLogsOrRethrows(catchClause, semanticModel, cancellationToken)))
            {
                return true;
            }

            if (IsExpectedJsDisconnectedCleanupCatch(tryStatement, invocation, semanticModel, methodSymbol, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsFailureProneOperationMeaningfullyHandled(
        SyntaxNode operation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        foreach (var tryStatement in operation.Ancestors().OfType<TryStatementSyntax>())
        {
            if (!tryStatement.Block.FullSpan.Contains(operation.Span) || tryStatement.Catches.Count == 0)
            {
                continue;
            }

            if (tryStatement.Catches.Any(catchClause => CatchClauseLogsOrRethrows(catchClause, semanticModel, cancellationToken)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExpectedJsDisconnectedCleanupCatch(
        TryStatementSyntax tryStatement,
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        IMethodSymbol methodSymbol,
        CancellationToken cancellationToken)
    {
        if (!IsDisposeMethod(methodSymbol) ||
            invocation.Expression is not MemberAccessExpressionSyntax memberAccess ||
            memberAccess.Name.Identifier.ValueText != "DisposeAsync" ||
            !IsKnownJsInteropInvocation(memberAccess, semanticModel, cancellationToken))
        {
            return false;
        }

        return IsJsDisconnectedExceptionCatchSet(tryStatement.Catches, semanticModel, cancellationToken);
    }

    private static bool IsJsDisconnectedExceptionCatchSet(
        SyntaxList<CatchClauseSyntax> catches,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (catches.Count == 0)
        {
            return false;
        }

        foreach (var catchClause in catches)
        {
            if (catchClause.Declaration?.Type is not { } catchType ||
                !IsJsDisconnectedExceptionType(catchType, semanticModel, cancellationToken))
            {
                return false;
            }
        }

        return true;
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

    private static bool IsWithinInteractivityGuard(
        SyntaxNode node,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        for (SyntaxNode? current = node, previous = node; current is not null; previous = current, current = current.Parent)
        {
            if (current is MethodDeclarationSyntax)
            {
                return false;
            }

            if (current is BlockSyntax block &&
                previous is StatementSyntax statement &&
                HasGuardClauseBefore(block, statement, semanticModel, containingType, cancellationToken))
            {
                return true;
            }

            if (current is IfStatementSyntax ifStatement)
            {
                if (IsNodeInsideStatement(previous, ifStatement.Statement) &&
                    ConditionGuaranteesInteractivity(ifStatement.Condition, whenTrue: true, semanticModel, containingType, cancellationToken))
                {
                    return true;
                }

                if (ifStatement.Else is { } elseClause &&
                    IsNodeInsideStatement(previous, elseClause.Statement) &&
                    ConditionGuaranteesInteractivity(ifStatement.Condition, whenTrue: false, semanticModel, containingType, cancellationToken))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasGuardClauseBefore(
        BlockSyntax block,
        StatementSyntax statement,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        foreach (var precedingStatement in block.Statements)
        {
            if (ReferenceEquals(precedingStatement, statement))
            {
                break;
            }

            if (IsInteractivityGuardClause(precedingStatement, semanticModel, containingType, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInteractivityGuardClause(
        StatementSyntax statement,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        if (statement is not IfStatementSyntax { Else: null } ifStatement ||
            !StatementDefinitelyExits(ifStatement.Statement))
        {
            return false;
        }

        return ConditionGuaranteesInteractivity(ifStatement.Condition, whenTrue: false, semanticModel, containingType, cancellationToken);
    }

    private static bool StatementDefinitelyExits(StatementSyntax statement) =>
        statement switch
        {
            ReturnStatementSyntax => true,
            ThrowStatementSyntax => true,
            BlockSyntax block when block.Statements.Count > 0 => StatementDefinitelyExits(block.Statements[block.Statements.Count - 1]),
            _ => false
        };

    private static bool IsNodeInsideStatement(SyntaxNode node, StatementSyntax statement) =>
        statement.FullSpan.Contains(node.Span);

    private static bool ConditionGuaranteesInteractivity(
        ExpressionSyntax expression,
        bool whenTrue,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        return ConditionGuaranteesInteractivity(
            expression,
            whenTrue,
            semanticModel,
            containingType,
            cancellationToken,
            new HashSet<ISymbol>(SymbolEqualityComparer.Default));
    }

    private static bool ConditionGuaranteesInteractivity(
        ExpressionSyntax expression,
        bool whenTrue,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken,
        HashSet<ISymbol> visitedSymbols)
    {
        expression = StripParentheses(expression);

        if (TryEvaluateRecognizedInteractivityValue(expression, whenTrue, semanticModel, containingType, cancellationToken, visitedSymbols, out var directResult))
        {
            return directResult;
        }

        switch (expression)
        {
            case PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression, Operand: var operand }:
                return ConditionGuaranteesInteractivity(operand, !whenTrue, semanticModel, containingType, cancellationToken, visitedSymbols);

            case BinaryExpressionSyntax binaryExpression when binaryExpression.IsKind(SyntaxKind.LogicalAndExpression):
                if (whenTrue)
                {
                    return ConditionGuaranteesInteractivity(binaryExpression.Left, whenTrue: true, semanticModel, containingType, cancellationToken, visitedSymbols) ||
                           ConditionGuaranteesInteractivity(binaryExpression.Right, whenTrue: true, semanticModel, containingType, cancellationToken, visitedSymbols);
                }

                return false;

            case BinaryExpressionSyntax binaryExpression when binaryExpression.IsKind(SyntaxKind.LogicalOrExpression):
                if (whenTrue)
                {
                    return ConditionGuaranteesInteractivity(binaryExpression.Left, whenTrue: true, semanticModel, containingType, cancellationToken, visitedSymbols) &&
                           ConditionGuaranteesInteractivity(binaryExpression.Right, whenTrue: true, semanticModel, containingType, cancellationToken, visitedSymbols);
                }

                return ConditionGuaranteesInteractivity(binaryExpression.Left, whenTrue: false, semanticModel, containingType, cancellationToken, visitedSymbols) ||
                       ConditionGuaranteesInteractivity(binaryExpression.Right, whenTrue: false, semanticModel, containingType, cancellationToken, visitedSymbols);

            case BinaryExpressionSyntax binaryExpression:
                return TryEvaluateBooleanComparison(binaryExpression, whenTrue, semanticModel, containingType, cancellationToken, visitedSymbols, out var comparisonResult) &&
                       comparisonResult;

            case IsPatternExpressionSyntax isPatternExpression:
                return TryEvaluatePatternInteractivity(isPatternExpression, whenTrue, semanticModel, containingType, cancellationToken, visitedSymbols, out var patternResult) &&
                       patternResult;

            default:
                return false;
        }
    }

    private static bool TryEvaluateBooleanComparison(
        BinaryExpressionSyntax expression,
        bool whenTrue,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken,
        HashSet<ISymbol> visitedSymbols,
        out bool result)
    {
        result = false;
        var left = StripParentheses(expression.Left);
        var right = StripParentheses(expression.Right);

        if (TryGetBooleanLiteralValue(left, out var leftLiteral))
        {
            return TryEvaluateBooleanLiteralComparison(
                right,
                leftLiteral,
                expression.Kind(),
                whenTrue,
                semanticModel,
                containingType,
                cancellationToken,
                visitedSymbols,
                out result);
        }

        if (TryGetBooleanLiteralValue(right, out var rightLiteral))
        {
            return TryEvaluateBooleanLiteralComparison(
                left,
                rightLiteral,
                expression.Kind(),
                whenTrue,
                semanticModel,
                containingType,
                cancellationToken,
                visitedSymbols,
                out result);
        }

        if (TryGetNullLiteralSide(left, right, out var candidateExpression) ||
            TryGetNullLiteralSide(right, left, out candidateExpression))
        {
            if (!IsAssignedRenderModeReference(candidateExpression, semanticModel, containingType, cancellationToken))
            {
                return false;
            }

            result = expression.Kind() switch
            {
                SyntaxKind.NotEqualsExpression => whenTrue,
                SyntaxKind.EqualsExpression => !whenTrue,
                _ => false
            };
            return expression.IsKind(SyntaxKind.NotEqualsExpression) || expression.IsKind(SyntaxKind.EqualsExpression);
        }

        return false;
    }

    private static bool TryEvaluateBooleanLiteralComparison(
        ExpressionSyntax expression,
        bool literalValue,
        SyntaxKind comparisonKind,
        bool whenTrue,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken,
        HashSet<ISymbol> visitedSymbols,
        out bool result)
    {
        result = false;
        if (!TryEvaluateRecognizedInteractivityValue(expression, whenTrue: literalValue, semanticModel, containingType, cancellationToken, visitedSymbols, out var baseValue))
        {
            return false;
        }

        result = comparisonKind switch
        {
            SyntaxKind.EqualsExpression => whenTrue ? baseValue : !baseValue,
            SyntaxKind.NotEqualsExpression => whenTrue ? !baseValue : baseValue,
            _ => false
        };
        return comparisonKind is SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;
    }

    private static bool TryEvaluatePatternInteractivity(
        IsPatternExpressionSyntax expression,
        bool whenTrue,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken,
        HashSet<ISymbol> visitedSymbols,
        out bool result)
    {
        result = false;
        if (!IsAssignedRenderModeReference(expression.Expression, semanticModel, containingType, cancellationToken))
        {
            return false;
        }

        switch (expression.Pattern)
        {
            case UnaryPatternSyntax { Pattern: ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal } } unaryPattern
                when literal.IsKind(SyntaxKind.NullLiteralExpression) && unaryPattern.IsKind(SyntaxKind.NotPattern):
                result = whenTrue;
                return true;

            case ConstantPatternSyntax { Expression: LiteralExpressionSyntax literal }
                when literal.IsKind(SyntaxKind.NullLiteralExpression):
                result = !whenTrue;
                return true;

            default:
                return false;
        }
    }

    private static bool TryEvaluateRecognizedInteractivityValue(
        ExpressionSyntax expression,
        bool whenTrue,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken,
        HashSet<ISymbol> visitedSymbols,
        out bool result)
    {
        result = false;
        expression = StripParentheses(expression);

        if (IsRendererInfoIsInteractiveReference(expression, semanticModel, cancellationToken))
        {
            result = whenTrue;
            return true;
        }

        if (TryGetSymbol(expression, semanticModel, cancellationToken) is not ISymbol symbol ||
            !visitedSymbols.Add(symbol))
        {
            return false;
        }

        try
        {
            switch (symbol)
            {
                case IMethodSymbol methodSymbol when methodSymbol.Parameters.Length == 0 &&
                                                    methodSymbol.ReturnType.SpecialType == SpecialType.System_Boolean &&
                                                    IsRelevantMethod(NormalizeMethodSymbol(methodSymbol), containingType):
                    if (TryGetSingleReturnedExpression(methodSymbol, cancellationToken) is { } methodExpression)
                    {
                        result = ConditionGuaranteesInteractivity(methodExpression, whenTrue, semanticModel, containingType, cancellationToken, visitedSymbols);
                        return true;
                    }

                    break;

                case IPropertySymbol propertySymbol when propertySymbol.Type.SpecialType == SpecialType.System_Boolean &&
                                                       SymbolEqualityComparer.Default.Equals(propertySymbol.ContainingType, containingType):
                    if (TryGetPropertyValueExpression(propertySymbol, cancellationToken) is { } propertyExpression)
                    {
                        result = ConditionGuaranteesInteractivity(propertyExpression, whenTrue, semanticModel, containingType, cancellationToken, visitedSymbols);
                        return true;
                    }

                    break;
            }
        }
        finally
        {
            visitedSymbols.Remove(symbol);
        }

        return false;
    }

    private static bool IsRendererInfoIsInteractiveReference(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        expression = StripParentheses(expression);

        if (expression is not MemberAccessExpressionSyntax memberAccess ||
            memberAccess.Name.Identifier.ValueText != "IsInteractive")
        {
            return false;
        }

        if (TryGetSymbol(memberAccess.Name, semanticModel, cancellationToken) is not IPropertySymbol propertySymbol ||
            propertySymbol.Type.SpecialType != SpecialType.System_Boolean)
        {
            return false;
        }

        if (TryGetSymbol(memberAccess.Expression, semanticModel, cancellationToken) is { Name: "RendererInfo" })
        {
            return true;
        }

        var receiverType = GetValueExpressionType(memberAccess.Expression, semanticModel, cancellationToken);
        return receiverType?.Name == "RendererInfo";
    }

    private static bool IsAssignedRenderModeReference(
        ExpressionSyntax expression,
        SemanticModel semanticModel,
        INamedTypeSymbol containingType,
        CancellationToken cancellationToken)
    {
        expression = StripParentheses(expression);
        if (TryGetSymbol(expression, semanticModel, cancellationToken) is not IPropertySymbol propertySymbol ||
            propertySymbol.Name != "AssignedRenderMode")
        {
            return false;
        }

        return SymbolEqualityComparer.Default.Equals(propertySymbol.ContainingType, containingType) ||
               propertySymbol.ContainingType.InheritsFromOrEquals(containingType) ||
               containingType.InheritsFromOrEquals(propertySymbol.ContainingType);
    }

    private static bool TryGetBooleanLiteralValue(ExpressionSyntax expression, out bool value)
    {
        expression = StripParentheses(expression);
        if (expression.IsKind(SyntaxKind.TrueLiteralExpression))
        {
            value = true;
            return true;
        }

        if (expression.IsKind(SyntaxKind.FalseLiteralExpression))
        {
            value = false;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetNullLiteralSide(ExpressionSyntax left, ExpressionSyntax right, out ExpressionSyntax candidateExpression)
    {
        left = StripParentheses(left);
        if (left.IsKind(SyntaxKind.NullLiteralExpression))
        {
            candidateExpression = right;
            return true;
        }

        candidateExpression = null!;
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
        if (IsExpectedJsDisconnectedCleanupCatch(catchClause, semanticModel, cancellationToken))
        {
            return true;
        }

        if (catchClause.Block.DescendantNodesAndSelf().OfType<ThrowStatementSyntax>().Any())
        {
            return true;
        }

        var caughtExceptionName = catchClause.Declaration?.Identifier.ValueText;
        foreach (var invocation in catchClause.Block.DescendantNodesAndSelf(static node => !IsNestedFunctionLike(node)).OfType<InvocationExpressionSyntax>())
        {
            if (!InvocationReportsCaughtException(invocation, caughtExceptionName, semanticModel, cancellationToken))
            {
                continue;
            }

            if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var memberName } &&
                (memberName.StartsWith("Log", StringComparison.Ordinal) || memberName is "TrackException" or "CaptureException" or "ReportException"))
            {
                return true;
            }

            var symbol = TryGetSymbol(invocation, semanticModel, cancellationToken);
            if (symbol is IMethodSymbol methodSymbol &&
                IsLoggingOrTelemetryMethod(methodSymbol))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExpectedJsDisconnectedCleanupCatch(
        CatchClauseSyntax catchClause,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (catchClause.Declaration?.Type is null ||
            !IsJsDisconnectedExceptionType(catchClause.Declaration.Type, semanticModel, cancellationToken))
        {
            return false;
        }

        if (catchClause.Ancestors().OfType<TryStatementSyntax>().FirstOrDefault() is not { } tryStatement ||
            catchClause.FirstAncestorOrSelf<MethodDeclarationSyntax>() is not { } methodDeclaration ||
            TryGetDeclaredMethodSymbol(methodDeclaration, semanticModel, cancellationToken) is not IMethodSymbol methodSymbol ||
            !IsDisposeMethod(NormalizeMethodSymbol(methodSymbol)))
        {
            return false;
        }

        return tryStatement.Block.DescendantNodesAndSelf(static node => !IsNestedFunctionLike(node))
            .OfType<InvocationExpressionSyntax>()
            .Any(invocation =>
                invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                memberAccess.Name.Identifier.ValueText == "DisposeAsync" &&
                IsKnownJsInteropInvocation(memberAccess, semanticModel, cancellationToken));
    }

    private static bool IsJsDisconnectedExceptionType(
        TypeSyntax typeSyntax,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (TryGetSymbol(typeSyntax, semanticModel, cancellationToken) is INamedTypeSymbol exceptionType &&
            GetTypeMetadataNames(exceptionType).Any(static name => name == "Microsoft.JSInterop.JSDisconnectedException"))
        {
            return true;
        }

        var typeName = typeSyntax.ToString();
        return typeName.EndsWith("JSDisconnectedException", StringComparison.Ordinal);
    }

    private static bool IsLoggingOrTelemetryMethod(IMethodSymbol methodSymbol)
    {
        if (methodSymbol.Name is "TrackException" or "CaptureException" or "ReportException")
        {
            return true;
        }

        return methodSymbol.Name.StartsWith("Log", StringComparison.Ordinal);
    }

    private static bool InvocationReportsCaughtException(
        InvocationExpressionSyntax invocation,
        string? caughtExceptionName,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(caughtExceptionName))
        {
            return false;
        }

        var caughtExceptionIdentifier = caughtExceptionName!;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (IsCaughtExceptionArgument(argument.Expression, caughtExceptionIdentifier, semanticModel, cancellationToken))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCaughtExceptionArgument(
        ExpressionSyntax expression,
        string caughtExceptionName,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        expression = StripParentheses(expression);

        if (expression is IdentifierNameSyntax identifierName &&
            string.Equals(identifierName.Identifier.ValueText, caughtExceptionName, StringComparison.Ordinal))
        {
            return true;
        }

        if (TryGetSymbol(expression, semanticModel, cancellationToken) is ILocalSymbol localSymbol &&
            string.Equals(localSymbol.Name, caughtExceptionName, StringComparison.Ordinal))
        {
            return true;
        }

        return IsExceptionType(GetValueExpressionType(expression, semanticModel, cancellationToken));
    }

    private static bool IsExceptionType(ITypeSymbol? typeSymbol)
    {
        for (var current = typeSymbol as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty) == "System.Exception")
            {
                return true;
            }
        }

        return false;
    }

    private static ExpressionSyntax? TryGetSingleReturnedExpression(
        IMethodSymbol methodSymbol,
        CancellationToken cancellationToken)
    {
        foreach (var syntaxReference in methodSymbol.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax(cancellationToken) is not MethodDeclarationSyntax methodDeclaration)
            {
                continue;
            }

            if (methodDeclaration.ExpressionBody is { Expression: { } expressionBody })
            {
                return expressionBody;
            }

            if (methodDeclaration.Body?.Statements.Count == 1 &&
                methodDeclaration.Body.Statements[0] is ReturnStatementSyntax { Expression: { } returnExpression })
            {
                return returnExpression;
            }
        }

        return null;
    }

    private static ExpressionSyntax? TryGetPropertyValueExpression(
        IPropertySymbol propertySymbol,
        CancellationToken cancellationToken)
    {
        foreach (var syntaxReference in propertySymbol.DeclaringSyntaxReferences)
        {
            if (syntaxReference.GetSyntax(cancellationToken) is not PropertyDeclarationSyntax propertyDeclaration)
            {
                continue;
            }

            if (propertyDeclaration.ExpressionBody is { Expression: { } expressionBody })
            {
                return expressionBody;
            }

            if (propertyDeclaration.AccessorList?.Accessors
                    .FirstOrDefault(static accessor => accessor.IsKind(SyntaxKind.GetAccessorDeclaration)) is { } getterAccessor)
            {
                if (getterAccessor.ExpressionBody is { Expression: { } getterExpressionBody })
                {
                    return getterExpressionBody;
                }

                if (getterAccessor.Body?.Statements.Count == 1 &&
                    getterAccessor.Body.Statements[0] is ReturnStatementSyntax { Expression: { } returnExpression })
                {
                    return returnExpression;
                }
            }
        }

        return null;
    }

    private static ExpressionSyntax StripParentheses(ExpressionSyntax expression)
    {
        while (expression is ParenthesizedExpressionSyntax parenthesizedExpression)
        {
            expression = parenthesizedExpression.Expression;
        }

        return expression;
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
        private HashSet<INamedTypeSymbol> lifecycleBoundaryProtectedComponents = new(SymbolEqualityComparer.Default);

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

        public bool IsLifecycleBoundaryProtected(INamedTypeSymbol componentSymbol, CancellationToken cancellationToken)
        {
            EnsureInitialized(cancellationToken);
            return lifecycleBoundaryProtectedComponents.Contains(componentSymbol);
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
                var boundaryProtectedComponentOwners = new ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>>(SymbolEqualityComparer.Default);
                var renderTreeAnalyses = new Dictionary<INamedTypeSymbol, RenderTreeAnalysis>(SymbolEqualityComparer.Default);
                var boundaryComponentNames = allComponentSymbols
                    .Select(symbol => symbol.Name)
                    .Concat(GetBoundaryComponentNames(compilation, errorBoundarySymbol))
                    .ToImmutableHashSet(StringComparer.Ordinal);

                foreach (var component in allComponentSymbols)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    allComponents.TryAdd(component, 0);

                    var declaredRenderMode = GetDeclaredRenderModeKey(component, renderModeAttributeSymbol);
                    declaredRenderModes.TryAdd(component, declaredRenderMode);

                    var renderTreeAnalysis = AnalyzeComponentRenderTree(component, boundaryComponentNames, cancellationToken);
                    renderTreeAnalyses[component] = renderTreeAnalysis;
                    if (renderTreeAnalysis.HasBoundaryRoot ||
                        renderTreeAnalysis.UncoveredRegions.Length > 0 ||
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

                    foreach (var childComponent in renderTreeAnalysis.BoundaryProtectedChildComponents)
                    {
                        var owners = boundaryProtectedComponentOwners.GetOrAdd(childComponent, static _ => new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default));
                        owners.TryAdd(component, 0);
                    }
                }

                AddDynamicBoundaryProtectedComponentOwners(
                    allComponentSymbols,
                    renderTreeAnalyses,
                    allComponents,
                    componentOwners,
                    boundaryProtectedComponentOwners,
                    cancellationToken);

                var effectiveRenderModes = ComputeEffectiveRenderModes(allComponents.Keys, declaredRenderModes, componentOwners);
                relevantComponents = ComputeRelevantBoundaryComponents(effectiveRenderModes, componentOwners, localRelevantBoundaryComponents);
                var boundaryProtectedRenderModes = ComputeBoundaryProtectedRenderModes(relevantComponents, effectiveRenderModes, localBoundaryComponents, componentOwners);
                boundaryProtectedComponents = ComputeBoundaryProtectedComponents(relevantComponents, effectiveRenderModes, boundaryProtectedRenderModes);
                lifecycleBoundaryProtectedComponents = ComputeLifecycleBoundaryProtectedComponents(effectiveRenderModes, boundaryProtectedRenderModes, componentOwners, boundaryProtectedComponentOwners);
                initialized = true;
            }
        }

        private RenderTreeAnalysis AnalyzeComponentRenderTree(
            INamedTypeSymbol componentSymbol,
            ImmutableHashSet<string> boundaryComponentNames,
            CancellationToken cancellationToken)
        {
            var childComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var generatedBoundaryProtectedChildComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var generatedBoundaryProtectedDynamicComponentTypeParameters = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            var renderBuilderHelperChildComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var hasBoundaryRoot = false;
            var boundaryRootHasErrorContent = true;
            var rootBoundaryIsKeyed = true;
            var rootBoundaryUsesStaleRouteKey = false;
            INamedTypeSymbol? rootBoundaryComponent = null;
            Location? boundaryRootLocation = null;
            var uncoveredRegions = ImmutableArray.CreateBuilder<InteractiveRenderRegion>();

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
                uncoveredRegions.AddRange(combinedRootAnalysis.UncoveredRegions);
                boundaryRootLocation ??= combinedRootAnalysis.BoundaryRootLocation;

                foreach (var childComponent in generatedAnalysis.ChildComponents)
                {
                    childComponents.Add(childComponent);
                }

                generatedBoundaryProtectedChildComponents.UnionWith(generatedAnalysis.BoundaryProtectedChildComponents);
                generatedBoundaryProtectedDynamicComponentTypeParameters.UnionWith(generatedAnalysis.BoundaryProtectedDynamicComponentTypeParameters);
                if (razorAnalysis is not null)
                {
                    foreach (var childComponent in ResolveRazorComponentNames(razorAnalysis.BoundaryProtectedComponentNames, cancellationToken))
                    {
                        childComponents.Add(childComponent);
                        generatedBoundaryProtectedChildComponents.Add(childComponent);
                    }

                    generatedBoundaryProtectedDynamicComponentTypeParameters.UnionWith(razorAnalysis.BoundaryProtectedDynamicComponentTypeParameters);
                }
            }

            foreach (var renderMethod in componentSymbol.GetMembers().OfType<IMethodSymbol>().Where(static method => method.Name != "BuildRenderTree" && IsRenderMethod(method)))
            {
                foreach (var syntaxReference in renderMethod.DeclaringSyntaxReferences)
                {
                    if (syntaxReference.GetSyntax(cancellationToken) is not MethodDeclarationSyntax methodDeclaration ||
                        methodDeclaration.Body is null)
                    {
                        continue;
                    }

                    var semanticModel = compilation.GetSemanticModel(methodDeclaration.SyntaxTree);
                    foreach (var childComponent in GetRenderedChildComponents(methodDeclaration.Body, semanticModel, componentBaseSymbol, cancellationToken))
                    {
                        childComponents.Add(childComponent);
                        renderBuilderHelperChildComponents.Add(childComponent);
                    }
                }
            }

            var boundaryProtectedChildComponents = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            boundaryProtectedChildComponents.UnionWith(generatedBoundaryProtectedChildComponents);
            if (hasBoundaryRoot)
            {
                boundaryProtectedChildComponents.UnionWith(renderBuilderHelperChildComponents);
            }

            return new RenderTreeAnalysis(
                hasBoundaryRoot,
                boundaryRootHasErrorContent,
                rootBoundaryIsKeyed,
                rootBoundaryUsesStaleRouteKey,
                rootBoundaryComponent,
                childComponents.ToImmutable(),
                boundaryProtectedChildComponents.ToImmutable(),
                generatedBoundaryProtectedDynamicComponentTypeParameters.ToImmutable(),
                uncoveredRegions.ToImmutable(),
                boundaryRootLocation);
        }

        private IEnumerable<INamedTypeSymbol> ResolveRazorComponentNames(
            IEnumerable<string> componentNames,
            CancellationToken cancellationToken)
        {
            var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var componentName in componentNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var symbol in compilation.GetSymbolsWithName(name => string.Equals(name, componentName, StringComparison.Ordinal), SymbolFilter.Type, cancellationToken))
                {
                    if (symbol is INamedTypeSymbol componentType &&
                        IsComponent(componentType, componentBaseSymbol) &&
                        !IsIgnoredRootComponent(componentType) &&
                        seen.Add(componentType))
                    {
                        yield return componentType;
                    }
                }
            }
        }

        private void AddDynamicBoundaryProtectedComponentOwners(
            IEnumerable<INamedTypeSymbol> componentSymbols,
            IReadOnlyDictionary<INamedTypeSymbol, RenderTreeAnalysis> renderTreeAnalyses,
            ConcurrentDictionary<INamedTypeSymbol, byte> allComponents,
            ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners,
            ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> boundaryProtectedComponentOwners,
            CancellationToken cancellationToken)
        {
            var dynamicBoundaryHosts = renderTreeAnalyses
                .Where(static pair => !pair.Value.BoundaryProtectedDynamicComponentTypeParameters.IsEmpty)
                .ToDictionary(
                    static pair => pair.Key,
                    static pair => pair.Value.BoundaryProtectedDynamicComponentTypeParameters,
                    SymbolEqualityComparer.Default);
            if (dynamicBoundaryHosts.Count == 0)
            {
                return;
            }

            foreach (var ownerComponent in componentSymbols)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var invocation in GetComponentInvocationExpressions(ownerComponent, cancellationToken))
                {
                    if (TryGetOpenedComponentType(invocation.Invocation, invocation.SemanticModel, cancellationToken) is not { } openedComponent ||
                        !dynamicBoundaryHosts.ContainsKey(openedComponent) ||
                        !IsDialogOpenInvocation(invocation.Invocation))
                    {
                        continue;
                    }

                    foreach (var targetComponent in GetTypeOfComponents(invocation.Invocation, invocation.SemanticModel, cancellationToken))
                    {
                        if (!IsComponent(targetComponent, componentBaseSymbol) ||
                            IsIgnoredRootComponent(targetComponent))
                        {
                            continue;
                        }

                        allComponents.TryAdd(targetComponent, 0);
                        componentOwners.GetOrAdd(targetComponent, static _ => new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default))
                            .TryAdd(ownerComponent, 0);
                        boundaryProtectedComponentOwners.GetOrAdd(targetComponent, static _ => new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default))
                            .TryAdd(ownerComponent, 0);
                    }
                }
            }
        }

        private IEnumerable<(InvocationExpressionSyntax Invocation, SemanticModel SemanticModel)> GetComponentInvocationExpressions(
            INamedTypeSymbol componentSymbol,
            CancellationToken cancellationToken)
        {
            foreach (var syntaxReference in componentSymbol.DeclaringSyntaxReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var syntax = syntaxReference.GetSyntax(cancellationToken);
                var semanticModel = compilation.GetSemanticModel(syntax.SyntaxTree);
                foreach (var invocation in syntax.DescendantNodes(static node => !IsNestedFunctionLike(node)).OfType<InvocationExpressionSyntax>())
                {
                    yield return (invocation, semanticModel);
                }
            }
        }

        private static bool IsDialogOpenInvocation(InvocationExpressionSyntax invocation) =>
            invocation.Expression switch
            {
                MemberAccessExpressionSyntax memberAccess => memberAccess.Name.Identifier.ValueText.StartsWith("Open", StringComparison.Ordinal),
                GenericNameSyntax genericName => genericName.Identifier.ValueText.StartsWith("Open", StringComparison.Ordinal),
                IdentifierNameSyntax identifierName => identifierName.Identifier.ValueText.StartsWith("Open", StringComparison.Ordinal),
                _ => false
            };

        private static IEnumerable<INamedTypeSymbol> GetTypeOfComponents(
            InvocationExpressionSyntax invocation,
            SemanticModel semanticModel,
            CancellationToken cancellationToken)
        {
            foreach (var typeOfExpression in invocation.ArgumentList.Arguments.SelectMany(static argument => argument.Expression.DescendantNodesAndSelf().OfType<TypeOfExpressionSyntax>()))
            {
                if (TryGetTypeInfo(typeOfExpression.Type, semanticModel, cancellationToken)?.Type is INamedTypeSymbol componentType)
                {
                    yield return componentType;
                }
            }
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
            bool isLifecycleMethod,
            bool isDisposeMethod,
            bool hasOperationalCode,
            bool hasFailureProneOperation,
            bool hasUnhandledFailureProneOperation,
            bool hasJsInteropCalls,
            bool hasUnhandledJsInteropCalls,
            bool hasUnguardedJsInteropCalls,
            ImmutableHashSet<IMethodSymbol> unhandledFailureProneCallees,
            ImmutableHashSet<IMethodSymbol> unguardedJsInteropCallees,
            bool isAsyncVoid,
            ImmutableArray<Location> catchWithoutLoggingLocations)
        {
            HasTryCatch = hasTryCatch;
            Callees = callees;
            DelegatedMethod = delegatedMethod;
            IsLifecycleMethod = isLifecycleMethod;
            IsDisposeMethod = isDisposeMethod;
            HasOperationalCode = hasOperationalCode;
            HasFailureProneOperation = hasFailureProneOperation;
            HasUnhandledFailureProneOperation = hasUnhandledFailureProneOperation;
            HasJsInteropCalls = hasJsInteropCalls;
            HasUnhandledJsInteropCalls = hasUnhandledJsInteropCalls;
            HasUnguardedJsInteropCalls = hasUnguardedJsInteropCalls;
            UnhandledFailureProneCallees = unhandledFailureProneCallees;
            UnguardedJsInteropCallees = unguardedJsInteropCallees;
            IsAsyncVoid = isAsyncVoid;
            CatchWithoutLoggingLocations = catchWithoutLoggingLocations;
        }

        public bool HasTryCatch { get; }

        public ImmutableHashSet<IMethodSymbol> Callees { get; }

        public IMethodSymbol? DelegatedMethod { get; }

        public bool IsLifecycleMethod { get; }

        public bool IsDisposeMethod { get; }

        public bool HasOperationalCode { get; }

        public bool HasFailureProneOperation { get; }

        public bool HasUnhandledFailureProneOperation { get; }

        public bool HasJsInteropCalls { get; }

        public bool HasUnhandledJsInteropCalls { get; }

        public bool HasUnguardedJsInteropCalls { get; }

        public ImmutableHashSet<IMethodSymbol> UnhandledFailureProneCallees { get; }

        public ImmutableHashSet<IMethodSymbol> UnguardedJsInteropCallees { get; }

        public bool IsAsyncVoid { get; }

        public ImmutableArray<Location> CatchWithoutLoggingLocations { get; }
    }

    private sealed class DynamicComponentReference
    {
        public DynamicComponentReference(INamedTypeSymbol? componentType, string? typeParameterName)
        {
            ComponentType = componentType;
            TypeParameterName = typeParameterName;
        }

        public INamedTypeSymbol? ComponentType { get; }

        public string? TypeParameterName { get; }
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
            ImmutableArray<InteractiveRenderRegion> uncoveredRegions,
            Location? boundaryRootLocation)
            : this(
                hasBoundaryRoot,
                boundaryRootHasErrorContent,
                rootBoundaryIsKeyed,
                rootBoundaryUsesStaleRouteKey,
                rootBoundaryComponent,
                childComponents,
                ImmutableHashSet<INamedTypeSymbol>.Empty,
                ImmutableHashSet<string>.Empty,
                uncoveredRegions,
                boundaryRootLocation)
        {
        }

        public RenderTreeAnalysis(
            bool hasBoundaryRoot,
            bool boundaryRootHasErrorContent,
            bool rootBoundaryIsKeyed,
            bool rootBoundaryUsesStaleRouteKey,
            INamedTypeSymbol? rootBoundaryComponent,
            ImmutableHashSet<INamedTypeSymbol> childComponents,
            ImmutableHashSet<INamedTypeSymbol> boundaryProtectedChildComponents,
            ImmutableHashSet<string> boundaryProtectedDynamicComponentTypeParameters,
            ImmutableArray<InteractiveRenderRegion> uncoveredRegions,
            Location? boundaryRootLocation)
        {
            HasBoundaryRoot = hasBoundaryRoot;
            BoundaryRootHasErrorContent = boundaryRootHasErrorContent;
            RootBoundaryIsKeyed = rootBoundaryIsKeyed;
            RootBoundaryUsesStaleRouteKey = rootBoundaryUsesStaleRouteKey;
            RootBoundaryComponent = rootBoundaryComponent;
            ChildComponents = childComponents;
            BoundaryProtectedChildComponents = boundaryProtectedChildComponents;
            BoundaryProtectedDynamicComponentTypeParameters = boundaryProtectedDynamicComponentTypeParameters;
            UncoveredRegions = uncoveredRegions;
            BoundaryRootLocation = boundaryRootLocation;
        }

        public bool HasBoundaryRoot { get; }

        public bool BoundaryRootHasErrorContent { get; }

        public bool RootBoundaryIsKeyed { get; }

        public bool RootBoundaryUsesStaleRouteKey { get; }

        public INamedTypeSymbol? RootBoundaryComponent { get; }

        public ImmutableHashSet<INamedTypeSymbol> ChildComponents { get; }

        public ImmutableHashSet<INamedTypeSymbol> BoundaryProtectedChildComponents { get; }

        public ImmutableHashSet<string> BoundaryProtectedDynamicComponentTypeParameters { get; }

        public ImmutableArray<InteractiveRenderRegion> UncoveredRegions { get; }

        public Location? BoundaryRootLocation { get; }
    }

    private sealed class CombinedRootAnalysis
    {
        public CombinedRootAnalysis(
            bool hasBoundaryRoot,
            bool boundaryRootHasErrorContent,
            bool rootBoundaryIsKeyed,
            bool rootBoundaryUsesStaleRouteKey,
            ImmutableArray<InteractiveRenderRegion> uncoveredRegions,
            Location? boundaryRootLocation)
        {
            HasBoundaryRoot = hasBoundaryRoot;
            BoundaryRootHasErrorContent = boundaryRootHasErrorContent;
            RootBoundaryIsKeyed = rootBoundaryIsKeyed;
            RootBoundaryUsesStaleRouteKey = rootBoundaryUsesStaleRouteKey;
            UncoveredRegions = uncoveredRegions;
            BoundaryRootLocation = boundaryRootLocation;
        }

        public bool HasBoundaryRoot { get; }

        public bool BoundaryRootHasErrorContent { get; }

        public bool RootBoundaryIsKeyed { get; }

        public bool RootBoundaryUsesStaleRouteKey { get; }

        public ImmutableArray<InteractiveRenderRegion> UncoveredRegions { get; }

        public Location? BoundaryRootLocation { get; }
    }

    private sealed class ComponentRenderAnalysis
    {
        public ComponentRenderAnalysis(ImmutableArray<InteractiveRenderRegion> uncoveredRegions, Location? boundaryLocation)
        {
            UncoveredRegions = uncoveredRegions;
            BoundaryLocation = boundaryLocation;
        }

        public ImmutableArray<InteractiveRenderRegion> UncoveredRegions { get; }

        public Location? BoundaryLocation { get; }
    }

    private enum LayoutBoundaryKeyAnalysis
    {
        Unknown,
        NavigationReactive,
        StaleNavigationSnapshot
    }
}

internal enum InteractiveRenderRegionKind
{
    HtmlEventHandler,
    ComponentCallback,
    ComponentBinding
}

internal readonly struct InteractiveRenderRegion
{
    private static readonly ImmutableHashSet<IMethodSymbol> EmptyRootMethods = ImmutableHashSet.Create<IMethodSymbol>(SymbolEqualityComparer.Default);

    public InteractiveRenderRegion(
        INamedTypeSymbol? sourceComponent,
        Location? diagnosticLocation,
        InteractiveRenderRegionKind kind,
        bool hasLocalBoundaryCoverage,
        string rootName,
        ImmutableHashSet<IMethodSymbol>? rootMethods = null)
    {
        SourceComponent = sourceComponent;
        DiagnosticLocation = diagnosticLocation;
        Kind = kind;
        HasLocalBoundaryCoverage = hasLocalBoundaryCoverage;
        RootName = rootName;
        RootMethods = rootMethods ?? EmptyRootMethods;
    }

    public INamedTypeSymbol? SourceComponent { get; }

    public Location? DiagnosticLocation { get; }

    public InteractiveRenderRegionKind Kind { get; }

    public bool HasLocalBoundaryCoverage { get; }

    public string RootName { get; }

    public ImmutableHashSet<IMethodSymbol> RootMethods { get; }

    public InteractiveRenderRegion WithSourceComponent(INamedTypeSymbol sourceComponent) =>
        new(sourceComponent, DiagnosticLocation, Kind, HasLocalBoundaryCoverage, RootName, RootMethods);

    public InteractiveRenderRegion WithDiagnosticLocation(Location diagnosticLocation) =>
        new(SourceComponent, diagnosticLocation, Kind, HasLocalBoundaryCoverage, RootName, RootMethods);

    public InteractiveRenderRegion WithRootMethods(ImmutableHashSet<IMethodSymbol> rootMethods) =>
        new(SourceComponent, DiagnosticLocation, Kind, HasLocalBoundaryCoverage, RootName, rootMethods);
}
