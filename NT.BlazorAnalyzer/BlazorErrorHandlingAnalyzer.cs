using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

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
        DiagnosticDescriptors.ErrorBoundaryMissingErrorContent
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterCompilationStartAction(static compilationStartContext =>
        {
            var componentBaseSymbol = compilationStartContext.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.ComponentBase");
            var errorBoundarySymbol = compilationStartContext.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.Web.ErrorBoundary");
            var renderModeAttributeSymbol = compilationStartContext.Compilation.GetTypeByMetadataName("Microsoft.AspNetCore.Components.RenderModeAttribute");

            if (componentBaseSymbol is null || errorBoundarySymbol is null || renderModeAttributeSymbol is null)
            {
                return;
            }

            var interactiveComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var allComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var declaredRenderModes = new ConcurrentDictionary<INamedTypeSymbol, string?>(SymbolEqualityComparer.Default);
            var localBoundaryComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var boundaryWithErrorContentComponents = new ConcurrentDictionary<INamedTypeSymbol, byte>(SymbolEqualityComparer.Default);
            var componentOwners = new ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>>(SymbolEqualityComparer.Default);
            var buildRenderTreeRootMethods = new ConcurrentDictionary<IMethodSymbol, byte>(SymbolEqualityComparer.Default);
            var methodAnalyses = new ConcurrentDictionary<IMethodSymbol, MethodAnalysis>(SymbolEqualityComparer.Default);

            compilationStartContext.RegisterSymbolAction(
                symbolContext => CollectCandidateComponent(symbolContext, componentBaseSymbol, renderModeAttributeSymbol, interactiveComponents, allComponents, declaredRenderModes),
                SymbolKind.NamedType);

            compilationStartContext.RegisterSyntaxNodeAction(
                syntaxContext => CollectMethodAnalysis(
                    syntaxContext,
                    componentBaseSymbol,
                    errorBoundarySymbol,
                    renderModeAttributeSymbol,
                    allComponents,
                    localBoundaryComponents,
                    boundaryWithErrorContentComponents,
                    componentOwners,
                    buildRenderTreeRootMethods,
                    methodAnalyses),
                SyntaxKind.MethodDeclaration);

            compilationStartContext.RegisterCompilationEndAction(
                compilationEndContext => AnalyzeCompilationEnd(
                    compilationEndContext,
                    allComponents,
                    declaredRenderModes,
                    interactiveComponents,
                    localBoundaryComponents,
                    boundaryWithErrorContentComponents,
                    componentOwners,
                    buildRenderTreeRootMethods,
                    methodAnalyses));
        });
    }

    private static void CollectCandidateComponent(
        SymbolAnalysisContext context,
        INamedTypeSymbol componentBaseSymbol,
        INamedTypeSymbol renderModeAttributeSymbol,
        ConcurrentDictionary<INamedTypeSymbol, byte> interactiveComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> allComponents,
        ConcurrentDictionary<INamedTypeSymbol, string?> declaredRenderModes)
    {
        var namedType = (INamedTypeSymbol)context.Symbol;
        if (!IsComponent(namedType, componentBaseSymbol))
        {
            return;
        }

        allComponents.TryAdd(namedType, 0);
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
        INamedTypeSymbol errorBoundarySymbol,
        INamedTypeSymbol renderModeAttributeSymbol,
        ConcurrentDictionary<INamedTypeSymbol, byte> allComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> localBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> boundaryWithErrorContentComponents,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners,
        ConcurrentDictionary<IMethodSymbol, byte> buildRenderTreeRootMethods,
        ConcurrentDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        if (context.Node is not MethodDeclarationSyntax methodDeclaration ||
            context.SemanticModel.GetDeclaredSymbol(methodDeclaration, context.CancellationToken) is not IMethodSymbol declaredMethod)
        {
            return;
        }

        var methodSymbol = NormalizeMethodSymbol(declaredMethod);
        var containingType = methodSymbol.ContainingType;
        if (!IsComponent(containingType, componentBaseSymbol))
        {
            return;
        }

        if (methodSymbol.Name == "BuildRenderTree")
        {
            if (methodDeclaration.Body is null)
            {
                return;
            }

            var renderTreeAnalysis = AnalyzeBuildRenderTree(methodDeclaration.Body, context.SemanticModel, componentBaseSymbol, errorBoundarySymbol, context.CancellationToken);
            if (renderTreeAnalysis.HasBoundaryRoot)
            {
                localBoundaryComponents.TryAdd(containingType, 0);
                if (renderTreeAnalysis.BoundaryRootHasErrorContent)
                {
                    boundaryWithErrorContentComponents.TryAdd(containingType, 0);
                }
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

            return;
        }

        if (GetDeclaredRenderModeKey(containingType, renderModeAttributeSymbol) is null ||
            !IsRelevantMethod(methodSymbol, containingType))
        {
            return;
        }

        methodAnalyses[methodSymbol] = AnalyzeMethod(methodDeclaration, methodSymbol, context.SemanticModel, context.CancellationToken);
    }

    private static void AnalyzeCompilationEnd(
        CompilationAnalysisContext context,
        ConcurrentDictionary<INamedTypeSymbol, byte> allComponents,
        ConcurrentDictionary<INamedTypeSymbol, string?> declaredRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, byte> interactiveComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> localBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, byte> boundaryWithErrorContentComponents,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners,
        ConcurrentDictionary<IMethodSymbol, byte> buildRenderTreeRootMethods,
        ConcurrentDictionary<IMethodSymbol, MethodAnalysis> methodAnalyses)
    {
        var effectiveRenderModes = ComputeEffectiveRenderModes(allComponents.Keys, declaredRenderModes, componentOwners);
        var relevantComponents = ComputeRelevantBoundaryComponents(effectiveRenderModes, componentOwners);
        var boundaryProtectedComponents = ComputeBoundaryProtectedComponents(relevantComponents, effectiveRenderModes, localBoundaryComponents, componentOwners);
        var suggestedBoundaryResolvers = ComputeSuggestedBoundaryResolvers(relevantComponents, effectiveRenderModes, componentOwners);

        foreach (var component in relevantComponents.OrderBy(static type => type.ToDisplayString(), StringComparer.Ordinal))
        {
            var boundaryProtected = boundaryProtectedComponents.Contains(component);
            var typeLocation = component.Locations.FirstOrDefault(static location => location.IsInSource);

            if (!boundaryProtected && typeLocation is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.MissingErrorBoundary,
                    typeLocation,
                    component.Name,
                    suggestedBoundaryResolvers.TryGetValue(component, out var resolver) ? resolver.Name : component.Name));
            }

            if (localBoundaryComponents.ContainsKey(component) &&
                !boundaryWithErrorContentComponents.ContainsKey(component) &&
                typeLocation is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.ErrorBoundaryMissingErrorContent,
                    typeLocation,
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
                foreach (var method in unsafeMethods.OrderBy(static method => method.Locations.FirstOrDefault(static location => location.IsInSource)?.GetLineSpan().StartLinePosition.Line ?? int.MaxValue))
                {
                    if (methodsWithSpecificTryCatchDiagnostics.Contains(method))
                    {
                        continue;
                    }

                    var methodLocation = method.Locations.FirstOrDefault(static location => location.IsInSource);
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

            foreach (var method in methods.OrderBy(static method => method.Locations.FirstOrDefault(static location => location.IsInSource)?.GetLineSpan().StartLinePosition.Line ?? int.MaxValue))
            {
                var analysis = methodAnalyses[method];
                var methodLocation = method.Locations.FirstOrDefault(static location => location.IsInSource);
                if (methodLocation is null)
                {
                    continue;
                }

                if (analysis.IsLifecycleMethod && analysis.HasOperationalCode && !analysis.HasTryCatch)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.LifecycleMissingTryCatch,
                        methodLocation,
                        method.Name,
                        component.Name));
                }

                if (analysis.IsDisposeMethod && analysis.HasOperationalCode && !analysis.HasTryCatch)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.DisposeMissingTryCatch,
                        methodLocation,
                        method.Name,
                        component.Name));
                }

                if (analysis.HasJsInteropCalls && !analysis.HasTryCatch)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.JsInteropMissingTryCatch,
                        methodLocation,
                        method.Name,
                        component.Name));
                }

                if (analysis.HasUnsafeLifecycleJsInterop)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.JsInteropRequiresInteractivityGuard,
                        methodLocation,
                        method.Name,
                        component.Name));
                }

                if (analysis.IsAsyncVoid)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.AsyncVoidMethod,
                        methodLocation,
                        method.Name,
                        component.Name));
                }

                foreach (var catchLocation in analysis.CatchWithoutLoggingLocations)
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.CatchWithoutLogging,
                        catchLocation,
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
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners)
    {
        var relevantComponents = new HashSet<INamedTypeSymbol>(
            effectiveRenderModes
                .Where(static pair => pair.Value.Any(static renderMode => !string.Equals(renderMode, StaticRenderModeKey, StringComparison.Ordinal)))
                .Select(static pair => pair.Key),
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
        }

        return relevantComponents;
    }

    private static HashSet<INamedTypeSymbol> ComputeBoundaryProtectedComponents(
        IEnumerable<INamedTypeSymbol> relevantComponents,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, byte> localBoundaryComponents,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners)
    {
        var relevantComponentSet = new HashSet<INamedTypeSymbol>(relevantComponents, SymbolEqualityComparer.Default);
        var protectedRenderModes = new Dictionary<INamedTypeSymbol, ImmutableHashSet<string>.Builder>(SymbolEqualityComparer.Default);
        foreach (var component in relevantComponentSet)
        {
            protectedRenderModes[component] = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
        }

        foreach (var component in relevantComponentSet)
        {
            if (localBoundaryComponents.ContainsKey(component))
            {
                protectedRenderModes[component].UnionWith(effectiveRenderModes[component]);
            }
        }

        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var component in relevantComponentSet)
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

                    var allOwnersCovered = true;
                    foreach (var owner in owners.Keys)
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

        return new HashSet<INamedTypeSymbol>(
            relevantComponentSet.Where(component => effectiveRenderModes[component].All(renderMode => protectedRenderModes[component].Contains(renderMode))),
            SymbolEqualityComparer.Default);
    }

    private static Dictionary<INamedTypeSymbol, INamedTypeSymbol> ComputeSuggestedBoundaryResolvers(
        IEnumerable<INamedTypeSymbol> relevantComponents,
        IReadOnlyDictionary<INamedTypeSymbol, ImmutableHashSet<string>> effectiveRenderModes,
        ConcurrentDictionary<INamedTypeSymbol, ConcurrentDictionary<INamedTypeSymbol, byte>> componentOwners)
    {
        var relevantComponentSet = new HashSet<INamedTypeSymbol>(relevantComponents, SymbolEqualityComparer.Default);
        var resolverByRenderMode = new Dictionary<INamedTypeSymbol, Dictionary<string, INamedTypeSymbol>>(SymbolEqualityComparer.Default);
        foreach (var component in relevantComponentSet)
        {
            var renderModeResolvers = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
            foreach (var renderMode in effectiveRenderModes[component])
            {
                renderModeResolvers[renderMode] = component;
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
                        ? owners.Keys.Where(owner => effectiveRenderModes.TryGetValue(owner, out var ownerRenderModes) && ownerRenderModes.Contains(renderMode)).ToArray()
                        : [];

                    var resolver = component;
                    if (ownersWithSameRenderMode.Length > 0)
                    {
                        var ownerResolvers = ownersWithSameRenderMode
                            .Select(owner => resolverByRenderMode[owner][renderMode])
                            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                            .ToArray();

                        if (ownerResolvers.Length == 1)
                        {
                            resolver = ownerResolvers[0];
                        }
                    }

                    if (!SymbolEqualityComparer.Default.Equals(resolverByRenderMode[component][renderMode], resolver))
                    {
                        resolverByRenderMode[component][renderMode] = resolver;
                        changed = true;
                    }
                }
            }
        }

        var suggestedResolvers = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var component in relevantComponentSet)
        {
            var distinctResolvers = resolverByRenderMode[component]
                .Values
                .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                .ToArray();

            suggestedResolvers[component] = distinctResolvers.Length == 1 ? distinctResolvers[0] : component;
        }

        return suggestedResolvers;
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
            if (childComponent is not null && childComponent.InheritsFromOrEquals(componentBaseSymbol))
            {
                childComponents.Add(childComponent);
            }
        }

        var hasUnprotectedInteractiveRoot = false;
        var hasBoundaryProtectedContent = false;
        var boundaryRootHasErrorContent = true;
        RootAnalysisState? currentRoot = null;

        foreach (var statement in body.Statements)
        {
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
                        currentRoot = RootAnalysisState.CreateComponentRoot(componentType, errorBoundarySymbol);
                    }
                    else
                    {
                        currentRoot.OpenComponent(componentType, errorBoundarySymbol);
                    }

                    break;
                }

                case "OpenElement":
                    if (currentRoot is null)
                    {
                        currentRoot = RootAnalysisState.CreateElementRoot();
                    }
                    else
                    {
                        currentRoot.OpenElement();
                    }
                    break;

                case "AddAttribute":
                    currentRoot?.AnalyzeAttribute(invocation, semanticModel, cancellationToken);
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
                        currentRoot = null;
                    }

                    break;
            }
        }

        if (currentRoot is not null)
        {
            hasUnprotectedInteractiveRoot |= currentRoot.HasUnprotectedInteractiveContent;
            hasBoundaryProtectedContent |= currentRoot.HasBoundaryProtectedContent;
            boundaryRootHasErrorContent &= !currentRoot.HasBoundaryMissingErrorContent;
        }

        return new RenderTreeAnalysis(
            hasBoundaryRoot: hasBoundaryProtectedContent && !hasUnprotectedInteractiveRoot,
            boundaryRootHasErrorContent,
            childComponents.ToImmutable());
    }

    private sealed class RootAnalysisState
    {
        private readonly Stack<bool> boundaryNodeStack = new();
        private int activeBoundaryCount;
        private bool rootBoundaryMissingErrorContent;
        private bool rootBoundaryHasErrorContent;

        private RootAnalysisState(bool ignoredRoot, bool boundaryRoot)
        {
            IgnoredRoot = ignoredRoot;
            BoundaryRoot = boundaryRoot;
            boundaryNodeStack.Push(boundaryRoot);
            activeBoundaryCount = boundaryRoot ? 1 : 0;
            HasBoundaryProtectedContent = boundaryRoot;
        }

        public bool BoundaryRoot { get; }

        public bool IgnoredRoot { get; }

        public bool HasBoundaryProtectedContent { get; private set; }

        public bool HasUnprotectedInteractiveContent { get; private set; }

        public bool HasBoundaryMissingErrorContent => rootBoundaryMissingErrorContent;

        public bool IsComplete => boundaryNodeStack.Count == 0;

        public static RootAnalysisState CreateElementRoot() => new(ignoredRoot: false, boundaryRoot: false);

        public static RootAnalysisState CreateComponentRoot(INamedTypeSymbol? componentType, INamedTypeSymbol errorBoundarySymbol)
        {
            if (IsIgnoredRootComponent(componentType))
            {
                return new RootAnalysisState(ignoredRoot: true, boundaryRoot: false);
            }

            if (componentType is not null && componentType.InheritsFromOrEquals(errorBoundarySymbol))
            {
                return new RootAnalysisState(ignoredRoot: false, boundaryRoot: true);
            }

            var root = new RootAnalysisState(ignoredRoot: false, boundaryRoot: false);
            root.HasUnprotectedInteractiveContent = true;
            return root;
        }

        public void OpenElement() => boundaryNodeStack.Push(false);

        public void OpenComponent(INamedTypeSymbol? componentType, INamedTypeSymbol errorBoundarySymbol)
        {
            if (IgnoredRoot || IsIgnoredRootComponent(componentType))
            {
                boundaryNodeStack.Push(false);
                return;
            }

            var isBoundary = componentType is not null && componentType.InheritsFromOrEquals(errorBoundarySymbol);
            boundaryNodeStack.Push(isBoundary);
            if (isBoundary)
            {
                activeBoundaryCount++;
                HasBoundaryProtectedContent = true;
                return;
            }

            if (activeBoundaryCount == 0)
            {
                HasUnprotectedInteractiveContent = true;
            }
        }

        public void AnalyzeAttribute(InvocationExpressionSyntax invocation, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (IgnoredRoot)
            {
                return;
            }

            if (BoundaryRoot && boundaryNodeStack.Count == 1 && HasAttributeNamed(invocation, semanticModel, cancellationToken, "ErrorContent"))
            {
                rootBoundaryHasErrorContent = true;
                return;
            }

            if (activeBoundaryCount == 0 && HasEventAttribute(invocation, semanticModel, cancellationToken))
            {
                HasUnprotectedInteractiveContent = true;
            }
        }

        public void CloseNode()
        {
            if (boundaryNodeStack.Count == 0)
            {
                return;
            }

            if (BoundaryRoot && boundaryNodeStack.Count == 1 && !rootBoundaryHasErrorContent)
            {
                rootBoundaryMissingErrorContent = true;
            }

            if (boundaryNodeStack.Pop())
            {
                activeBoundaryCount--;
            }
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

    private static bool HasEventAttribute(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        CancellationToken cancellationToken)
    {
        if (invocation.ArgumentList.Arguments.Count < 2)
        {
            return false;
        }

        var constantValue = semanticModel.GetConstantValue(invocation.ArgumentList.Arguments[1].Expression, cancellationToken);
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

        var constantValue = semanticModel.GetConstantValue(invocation.ArgumentList.Arguments[1].Expression, cancellationToken);
        return constantValue.HasValue &&
               constantValue.Value is string value &&
               string.Equals(value, attributeName, StringComparison.Ordinal);
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
            var symbolInfo = semanticModel.GetSymbolInfo(expression, cancellationToken);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

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

        var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
        var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();

        if (symbol is not IMethodSymbol methodSymbol)
        {
            return null;
        }

        methodSymbol = NormalizeMethodSymbol(methodSymbol);
        return IsRelevantMethod(methodSymbol, containingType) ? methodSymbol : null;
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

            var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
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

            var symbolInfo = semanticModel.GetSymbolInfo(invocation, cancellationToken);
            var symbol = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
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

        return semanticModel.GetTypeInfo(genericName.TypeArgumentList.Arguments[0], cancellationToken).Type as INamedTypeSymbol;
    }

    private static IMethodSymbol NormalizeMethodSymbol(IMethodSymbol methodSymbol) => methodSymbol.OriginalDefinition;

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
            ImmutableHashSet<INamedTypeSymbol> childComponents)
        {
            HasBoundaryRoot = hasBoundaryRoot;
            BoundaryRootHasErrorContent = boundaryRootHasErrorContent;
            ChildComponents = childComponents;
        }

        public bool HasBoundaryRoot { get; }

        public bool BoundaryRootHasErrorContent { get; }

        public ImmutableHashSet<INamedTypeSymbol> ChildComponents { get; }
    }
}
