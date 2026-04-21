using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class BlazorErrorHandlingAnalyzerWhiteBoxTests
{
    [Fact]
    public void TryGetComponentInteractiveRegionKind_ClassifiesCallbacksAndBindings()
    {
        Assert.Equal(
            InteractiveRenderRegionKind.ComponentCallback,
            GetInteractiveRegionKind("""__builder.AddAttribute(0, "OnSave", HandleSave);"""));
        Assert.Equal(
            InteractiveRenderRegionKind.ComponentCallback,
            GetInteractiveRegionKind("""__builder.AddAttribute(0, "OnSave", (Action)HandleSave);"""));
        Assert.Equal(
            InteractiveRenderRegionKind.ComponentCallback,
            GetInteractiveRegionKind("""__builder.AddAttribute(0, "OnSave", EventCallback.Factory.Create(this, HandleSave));"""));
        Assert.Equal(
            InteractiveRenderRegionKind.ComponentCallback,
            GetInteractiveRegionKind("""__builder.AddAttribute(0, "OnSave", () => HandleSave());"""));
        Assert.Equal(
            InteractiveRenderRegionKind.ComponentCallback,
            GetInteractiveRegionKind("""__builder.AddAttribute(0, "OnSave", delegate { HandleSave(); });"""));
        Assert.Equal(
            InteractiveRenderRegionKind.ComponentBinding,
            GetInteractiveRegionKind("""__builder.AddAttribute(0, "ValueChanged", EventCallback.Factory.CreateBinder(this, __value => currentValue = __value, currentValue));"""));
        Assert.Equal(
            InteractiveRenderRegionKind.ComponentBinding,
            GetInteractiveRegionKind("""__builder.AddAttribute(0, "ValueChanged", (Action<string>)HandleChange);"""));
        Assert.Equal(
            InteractiveRenderRegionKind.ComponentBinding,
            GetInteractiveRegionKind("""__builder.AddAttribute(0, "ValueChanged", EventCallback.Factory.Create<string>(this, HandleChange));"""));
        Assert.Null(GetInteractiveRegionKind("""__builder.AddAttribute(0, "ChildContent", (RenderFragment)(__builder2 => { }));"""));
        Assert.Null(GetInteractiveRegionKind("""__builder.AddAttribute(0, "RowTemplate", (RenderFragment<string>)(_ => __builder2 => { }));"""));
        Assert.Null(GetInteractiveRegionKind("""__builder.AddAttribute(0, "Plain", 42);"""));
        Assert.Equal(
            InteractiveRenderRegionKind.ComponentCallback,
            GetInteractiveRegionKind("""__builder.AddAttribute(0, dynamicAttributeName, HandleSave);"""));
        Assert.Null(GetInteractiveRegionKind("""__builder.AddAttribute(0, "OnSave");"""));
    }

    [Fact]
    public void IsBindingGeneratedComponentCallback_DetectsSupportedChangedPatterns()
    {
        Assert.False(IsBindingCallback("""__builder.AddAttribute(0, "OnSave", HandleSave);"""));
        Assert.True(IsBindingCallback("""__builder.AddAttribute(0, "ValueChanged", EventCallback.Factory.CreateBinder(this, __value => currentValue = __value, currentValue));"""));
        Assert.True(IsBindingCallback("""__builder.AddAttribute(0, "ValueChanged", (Action<string>)HandleChange);"""));
        Assert.True(IsBindingCallback("""__builder.AddAttribute(0, "ValueChanged", EventCallback.Factory.Create<string>(this, HandleChange));"""));
        Assert.False(IsBindingCallback("""__builder.AddAttribute(0, "ValueChanged", 42);"""));
        Assert.False(IsBindingCallback("""__builder.AddAttribute(0, dynamicAttributeName, EventCallback.Factory.Create<string>(this, HandleChange));"""));
        Assert.False(IsBindingCallback("""__builder.AddAttribute(0, "ValueChanged");"""));
    }

    [Fact]
    public void AttributeAndElementNameHelpers_HandleConstantAndDynamicArguments()
    {
        var attributeContext = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext(
            """
            __builder.AddAttribute(0, "OnSave", HandleSave);
            __builder.AddAttribute(1, dynamicAttributeName, HandleSave);
            __builder.AddAttribute(2, "OnSave");
            __builder.OpenElement(3, "button");
            __builder.OpenElement(4, dynamicElementName);
            __builder.OpenElement(5);
            """);

        var constantAttribute = attributeContext.FindInvocation("AddAttribute", "\"OnSave\", HandleSave");
        var dynamicAttribute = attributeContext.FindInvocation("AddAttribute", "dynamicAttributeName");
        var shortAttribute = attributeContext.FindInvocation("AddAttribute", "\"OnSave\")");
        var constantElement = attributeContext.FindInvocation("OpenElement", "\"button\"");
        var dynamicElement = attributeContext.FindInvocation("OpenElement", "dynamicElementName");
        var shortElement = attributeContext.FindInvocation("OpenElement", "(5)");

        Assert.Equal("OnSave", InvokeTryGetAttributeName(constantAttribute, attributeContext));
        Assert.Null(InvokeTryGetAttributeName(dynamicAttribute, attributeContext));
        Assert.Equal("OnSave", InvokeTryGetAttributeName(shortAttribute, attributeContext));
        Assert.Equal("button", InvokeTryGetElementName(constantElement, attributeContext));
        Assert.Null(InvokeTryGetElementName(dynamicElement, attributeContext));
        Assert.Null(InvokeTryGetElementName(shortElement, attributeContext));
    }

    [Fact]
    public void GetValueExpressionType_PrefersDirectType_ThenConvertedType_AndHandlesUnknown()
    {
        var context = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext(
            """
            __builder.AddAttribute(0, "OnSave", (Action)HandleSave);
            Action callback = () => HandleSave();
            __builder.AddAttribute(1, "Value", unknownValue);
            """);

        var directTypeExpression = context.FindInvocation("AddAttribute", "(Action)HandleSave").ArgumentList.Arguments[2].Expression;
        var convertedTypeExpression = context.FindExpression("() => HandleSave()");
        var foreignContext = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext("""__builder.AddAttribute(0, "Value", unknownValue);""");
        var foreignExpression = foreignContext.FindInvocation("AddAttribute", "unknownValue").ArgumentList.Arguments[2].Expression;

        Assert.Equal("System.Action", InvokeGetValueExpressionType(directTypeExpression, context)?.ToDisplayString());
        Assert.Equal("System.Action", InvokeGetValueExpressionType(convertedTypeExpression, context)?.ToDisplayString());
        Assert.Null(InvokeGetValueExpressionType(foreignExpression, context));
    }

    [Fact]
    public void IsCreateBinderInvocation_DetectsMemberAccessAndResolvedMethodName()
    {
        var context = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext(
            """
            var binder = EventCallback.Factory.CreateBinder(this, __value => currentValue = __value, currentValue);
            var local = CreateBinder();
            var other = OtherMethod();
            """);

        var memberAccessInvocation = context.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => invocation.ToString().Contains("Factory.CreateBinder", StringComparison.Ordinal));
        var localMethodInvocation = context.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => string.Equals(invocation.ToString(), "CreateBinder()", StringComparison.Ordinal));
        var otherInvocation = context.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => string.Equals(invocation.ToString(), "OtherMethod()", StringComparison.Ordinal));

        Assert.True(InvokeIsCreateBinderInvocation(memberAccessInvocation, context));
        Assert.True(InvokeIsCreateBinderInvocation(localMethodInvocation, context));
        Assert.False(InvokeIsCreateBinderInvocation(otherInvocation, context));
    }

    [Fact]
    public void MapUncoveredRegionsToRazorLocations_MapsMatchingAndFallbackLocations()
    {
        var htmlMarkup = "<button @onclick=\"Save\"></button>";
        var componentMarkup = "<Form.Editor @bind-Value=\"CurrentValue\" /><Toolbar />";
        var htmlLocation = AnalyzerWhiteBoxTestHarness.CreateLocation("Page.razor", htmlMarkup, "@onclick");
        var bindingLocation = AnalyzerWhiteBoxTestHarness.CreateLocation("Page.razor", componentMarkup, "@bind-Value");
        var rootLocation = AnalyzerWhiteBoxTestHarness.CreateLocation("Page.razor", componentMarkup, "Toolbar");
        var analysis = new RazorMarkupAnalysis(
            hasBoundaryRoot: false,
            boundaryRootHasErrorContent: false,
            boundaryRootIsKeyed: false,
            boundaryRootLocation: null,
            htmlInteractiveRegions:
            [
                new RazorMarkupRegion(InteractiveRenderRegionKind.HtmlEventHandler, "Button", htmlLocation)
            ],
            componentRoots:
            [
                new RazorComponentRoot("Form.Editor", AnalyzerWhiteBoxTestHarness.CreateLocation("Page.razor", componentMarkup, "Editor"), bindingLocation),
                new RazorComponentRoot("Toolbar", rootLocation, bindingLocation: null)
            ]);

        var regions = ImmutableArray.Create(
            new InteractiveRenderRegion(null, null, InteractiveRenderRegionKind.HtmlEventHandler, false, "button"),
            new InteractiveRenderRegion(null, null, InteractiveRenderRegionKind.ComponentBinding, false, "Editor"),
            new InteractiveRenderRegion(null, null, InteractiveRenderRegionKind.ComponentCallback, false, "UnknownRoot"),
            new InteractiveRenderRegion(null, null, InteractiveRenderRegionKind.ComponentCallback, false, "MissingRoot"));

        var mapped = (ImmutableArray<InteractiveRenderRegion>)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("MapUncoveredRegionsToRazorLocations", regions, analysis)!;

        Assert.Equal(htmlLocation.SourceSpan, mapped[0].DiagnosticLocation!.SourceSpan);
        Assert.Equal(bindingLocation.SourceSpan, mapped[1].DiagnosticLocation!.SourceSpan);
        Assert.Equal(rootLocation.SourceSpan, mapped[2].DiagnosticLocation!.SourceSpan);
        Assert.Null(mapped[3].DiagnosticLocation);
    }

    [Fact]
    public void CombineRootAnalysis_HonorsGeneratedAndRazorBoundaryRules()
    {
        var defaultLocation = AnalyzerWhiteBoxTestHarness.CreateLocation("Generated.razor.g.cs", "builder", "builder");
        var generatedLocation = AnalyzerWhiteBoxTestHarness.CreateLocation("Generated.razor.g.cs", "boundary", "boundary");
        var razorBoundaryLocation = AnalyzerWhiteBoxTestHarness.CreateLocation("Page.razor", "<ErrorBoundary />", "ErrorBoundary");
        var uncoveredRegions = ImmutableArray.Create(new InteractiveRenderRegion(null, defaultLocation, InteractiveRenderRegionKind.ComponentCallback, false, "Editor"));
        var emptyChildren = ImmutableHashSet<INamedTypeSymbol>.Empty;

        var generatedOnly = AnalyzerWhiteBoxTestHarness.CreatePrivateInstance(
            "RenderTreeAnalysis",
            false,
            true,
            false,
            false,
            null,
            emptyChildren,
            uncoveredRegions,
            generatedLocation);

        var combinedWithoutRazor = AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("CombineRootAnalysis", generatedOnly, null, defaultLocation)!;
        Assert.False(AnalyzerWhiteBoxTestHarness.GetProperty<bool>(combinedWithoutRazor, "HasBoundaryRoot"));
        Assert.Equal(generatedLocation.SourceSpan, AnalyzerWhiteBoxTestHarness.GetProperty<Location>(combinedWithoutRazor, "BoundaryRootLocation").SourceSpan);

        var trustedGenerated = AnalyzerWhiteBoxTestHarness.CreatePrivateInstance(
            "RenderTreeAnalysis",
            true,
            true,
            true,
            false,
            null,
            emptyChildren,
            ImmutableArray<InteractiveRenderRegion>.Empty,
            generatedLocation);
        var trustedRazor = new RazorMarkupAnalysis(
            hasBoundaryRoot: true,
            boundaryRootHasErrorContent: true,
            boundaryRootIsKeyed: true,
            boundaryRootLocation: razorBoundaryLocation,
            htmlInteractiveRegions: ImmutableArray<RazorMarkupRegion>.Empty,
            componentRoots: ImmutableArray<RazorComponentRoot>.Empty);
        var combinedTrusted = AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("CombineRootAnalysis", trustedGenerated, trustedRazor, defaultLocation)!;
        Assert.True(AnalyzerWhiteBoxTestHarness.GetProperty<bool>(combinedTrusted, "HasBoundaryRoot"));
        Assert.Equal(razorBoundaryLocation.SourceSpan, AnalyzerWhiteBoxTestHarness.GetProperty<Location>(combinedTrusted, "BoundaryRootLocation").SourceSpan);

        var razorOnlyBoundary = AnalyzerWhiteBoxTestHarness.CreatePrivateInstance(
            "RenderTreeAnalysis",
            false,
            true,
            true,
            true,
            null,
            emptyChildren,
            ImmutableArray<InteractiveRenderRegion>.Empty,
            generatedLocation);
        var mappedRazor = new RazorMarkupAnalysis(
            hasBoundaryRoot: true,
            boundaryRootHasErrorContent: true,
            boundaryRootIsKeyed: false,
            boundaryRootLocation: razorBoundaryLocation,
            htmlInteractiveRegions: ImmutableArray<RazorMarkupRegion>.Empty,
            componentRoots:
            [
                new RazorComponentRoot("Editor", razorBoundaryLocation, bindingLocation: null)
            ]);
        var combinedMapped = AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("CombineRootAnalysis", razorOnlyBoundary, mappedRazor, defaultLocation)!;
        Assert.True(AnalyzerWhiteBoxTestHarness.GetProperty<bool>(combinedMapped, "HasBoundaryRoot"));
        Assert.True(AnalyzerWhiteBoxTestHarness.GetProperty<bool>(combinedMapped, "BoundaryRootHasErrorContent"));
        Assert.False(AnalyzerWhiteBoxTestHarness.GetProperty<bool>(combinedMapped, "RootBoundaryIsKeyed"));
        Assert.True(AnalyzerWhiteBoxTestHarness.GetProperty<bool>(combinedMapped, "RootBoundaryUsesStaleRouteKey"));
    }

    [Fact]
    public void RootAnalysisState_TracksInteractiveRegionsAndBoundaryFlags()
    {
        var context = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext(
            """
            __builder.AddAttribute(0, "onclick", EventCallback.Factory.Create(this, HandleSave));
            __builder.AddAttribute(1, "onclick", EventCallback.Factory.Create(this, HandleSave));
            __builder.AddAttribute(2, "ErrorContent", "ignored");
            __builder.SetKey(3, RouteData.PageType);
            """,
            extraMembers: "public Microsoft.AspNetCore.Components.RouteData RouteData { get; } = new(typeof(TestComponent), []);");

        var rootAnalysisType = typeof(BlazorErrorHandlingAnalyzer).GetNestedType("RootAnalysisState", System.Reflection.BindingFlags.NonPublic)!;
        var createElementRoot = rootAnalysisType.GetMethod("CreateElementRoot", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var createComponentRoot = rootAnalysisType.GetMethod("CreateComponentRoot", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var openComponent = rootAnalysisType.GetMethod("OpenComponent")!;
        var analyzeAttribute = rootAnalysisType.GetMethod("AnalyzeAttribute")!;
        var analyzeSetKey = rootAnalysisType.GetMethod("AnalyzeSetKey")!;
        var closeNode = rootAnalysisType.GetMethod("CloseNode")!;

        var errorBoundarySymbol = context.GetRequiredType("Microsoft.AspNetCore.Components.Web.ErrorBoundary");
        var customBoundarySymbol = context.GetRequiredType("TestComponents.CustomBoundaryWithBuiltInErrorContent");

        var elementRoot = createElementRoot.Invoke(null, [Location.None, "button"])!;
        var htmlAttribute = context.FindInvocations("AddAttribute", "\"onclick\"").First();
        analyzeAttribute.Invoke(elementRoot, [htmlAttribute, context.SemanticModel, CancellationToken.None]);
        analyzeAttribute.Invoke(elementRoot, [htmlAttribute, context.SemanticModel, CancellationToken.None]);
        closeNode.Invoke(elementRoot, []);
        var elementRegions = (ImmutableArray<InteractiveRenderRegion>)rootAnalysisType.GetProperty("UncoveredRegions")!.GetValue(elementRoot)!;
        Assert.Single(elementRegions);

        var boundaryRoot = createComponentRoot.Invoke(null, [customBoundarySymbol, errorBoundarySymbol, CancellationToken.None, Location.None])!;
        var errorContentAttribute = context.FindInvocation("AddAttribute", "\"ErrorContent\"");
        var setKeyInvocation = context.FindInvocation("SetKey");
        analyzeAttribute.Invoke(boundaryRoot, [errorContentAttribute, context.SemanticModel, CancellationToken.None]);
        analyzeSetKey.Invoke(boundaryRoot, [setKeyInvocation, context.SemanticModel, CancellationToken.None]);
        openComponent.Invoke(boundaryRoot, [errorBoundarySymbol, errorBoundarySymbol, Location.None]);
        closeNode.Invoke(boundaryRoot, []);
        closeNode.Invoke(boundaryRoot, []);

        Assert.True((bool)rootAnalysisType.GetProperty("HasBoundaryProtectedContent")!.GetValue(boundaryRoot)!);
        Assert.True((bool)rootAnalysisType.GetProperty("RootBoundaryIsKeyed")!.GetValue(boundaryRoot)!);
        Assert.False((bool)rootAnalysisType.GetProperty("HasBoundaryMissingErrorContent")!.GetValue(boundaryRoot)!);
    }

    [Fact]
    public void HelperMethods_HandleEmptyLists_IgnoredRoots_AndForeignSyntax()
    {
        var pageTitleContext = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext("""__builder.OpenComponent<PageTitle>(0);""");
        var pageTitleSymbol = pageTitleContext.GetRequiredType("Microsoft.AspNetCore.Components.Web.PageTitle");
        var errorBoundarySymbol = pageTitleContext.GetRequiredType("Microsoft.AspNetCore.Components.Web.ErrorBoundary");
        var rootAnalysisType = typeof(BlazorErrorHandlingAnalyzer).GetNestedType("RootAnalysisState", System.Reflection.BindingFlags.NonPublic)!;
        var createComponentRoot = rootAnalysisType.GetMethod("CreateComponentRoot", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var openComponent = rootAnalysisType.GetMethod("OpenComponent")!;
        var closeNode = rootAnalysisType.GetMethod("CloseNode")!;

        var ignoredRoot = createComponentRoot.Invoke(null, [pageTitleSymbol, errorBoundarySymbol, CancellationToken.None, Location.None])!;
        Assert.True((bool)rootAnalysisType.GetProperty("IgnoredRoot")!.GetValue(ignoredRoot)!);
        openComponent.Invoke(ignoredRoot, [pageTitleSymbol, errorBoundarySymbol, Location.None]);
        closeNode.Invoke(ignoredRoot, []);
        closeNode.Invoke(ignoredRoot, []);
        closeNode.Invoke(ignoredRoot, []);
        Assert.True((bool)rootAnalysisType.GetProperty("IsComplete")!.GetValue(ignoredRoot)!);

        var emptyHtmlLocation = AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "TryConsumeHtmlRegionLocation",
            new InteractiveRenderRegion(null, null, InteractiveRenderRegionKind.HtmlEventHandler, false, "button"),
            new List<RazorMarkupRegion>());
        var emptyComponentLocation = AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "TryConsumeComponentRootLocation",
            new InteractiveRenderRegion(null, null, InteractiveRenderRegionKind.ComponentCallback, false, "Editor"),
            new List<RazorComponentRoot>());
        Assert.Null(emptyHtmlLocation);
        Assert.Null(emptyComponentLocation);

        var primaryContext = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext(
            """
            __builder.AddAttribute(0, "class", "value");
            __builder.AddAttribute(1, "ErrorContent", "value");
            var binder = OtherMethod();
            """);
        var foreignContext = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext("""__builder.AddAttribute(0, "ValueChanged", EventCallback.Factory.Create<string>(this, HandleChange));""");
        var classAttribute = primaryContext.FindInvocation("AddAttribute", "\"class\"");
        var errorContentAttribute = primaryContext.FindInvocation("AddAttribute", "\"ErrorContent\"");
        var foreignAttribute = foreignContext.FindInvocation("AddAttribute");
        var foreignValueExpression = foreignAttribute.ArgumentList.Arguments[2].Expression;
        var binderInvocation = primaryContext.Root.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Single(invocation => string.Equals(invocation.ToString(), "OtherMethod()", StringComparison.Ordinal));

        Assert.False((bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("HasEventAttribute", classAttribute, primaryContext.SemanticModel, CancellationToken.None)!);
        Assert.True((bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("HasAttributeNamed", errorContentAttribute, primaryContext.SemanticModel, CancellationToken.None, "ErrorContent")!);
        Assert.False((bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("HasAttributeNamed", classAttribute, primaryContext.SemanticModel, CancellationToken.None, "ErrorContent")!);
        Assert.Null((InteractiveRenderRegionKind?)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "TryGetComponentInteractiveRegionKind",
            foreignAttribute,
            primaryContext.SemanticModel,
            CancellationToken.None));
        Assert.False((bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "IsBindingGeneratedComponentCallback",
            foreignAttribute,
            foreignValueExpression,
            primaryContext.SemanticModel,
            CancellationToken.None)!);
        Assert.False((bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("IsCreateBinderInvocation", binderInvocation, foreignContext.SemanticModel, CancellationToken.None)!);
    }

    [Fact]
    public void MergeRootAnalysisState_AndRenderTreeAnalysis_CoverFallbackBranches()
    {
        var context = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext(
            """
            if (currentValue.Length > 0)
                __builder.OpenElement(0, "button");
            __builder.AddAttribute(1, "onclick", EventCallback.Factory.Create(this, HandleSave));
            __builder.SetKey(2, currentValue);
            __builder.CloseElement();
            """);
        var errorBoundarySymbol = context.GetRequiredType("Microsoft.AspNetCore.Components.Web.ErrorBoundary");
        var mergeMethod = AnalyzerWhiteBoxTestHarness.GetAnalyzerMethod("MergeRootAnalysisState", 8);
        var analyzeStatementsMethod = AnalyzerWhiteBoxTestHarness.GetAnalyzerMethod("AnalyzeBuildRenderTreeStatements", 5);
        var rootAnalysisType = typeof(BlazorErrorHandlingAnalyzer).GetNestedType("RootAnalysisState", System.Reflection.BindingFlags.NonPublic)!;
        var createBoundaryRoot = rootAnalysisType.GetMethod("CreateComponentRoot", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var boundaryRoot = createBoundaryRoot.Invoke(null, [errorBoundarySymbol, errorBoundarySymbol, CancellationToken.None, Location.None])!;
        var uncoveredBuilder = ImmutableArray.CreateBuilder<InteractiveRenderRegion>();
        object?[] mergeArgs = [boundaryRoot, uncoveredBuilder, false, true, true, false, null, null];

        mergeMethod.Invoke(null, mergeArgs);

        Assert.Same(errorBoundarySymbol, mergeArgs[6]);
        Assert.Equal(Location.None, mergeArgs[7]);

        var body = (Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax)context.Root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BlockSyntax>().Last();
        var analysis = analyzeStatementsMethod.Invoke(null, [body.Statements, ImmutableHashSet<INamedTypeSymbol>.Empty, context.SemanticModel, errorBoundarySymbol, CancellationToken.None])!;
        Assert.False(AnalyzerWhiteBoxTestHarness.GetProperty<bool>(analysis, "HasBoundaryRoot"));
        Assert.Empty(AnalyzerWhiteBoxTestHarness.GetProperty<ImmutableArray<InteractiveRenderRegion>>(analysis, "UncoveredRegions"));
    }

    [Fact]
    public void RemainingHelperBranches_HandleNullNames_ForeignSyntax_AndExpressionBodiedBoundaries()
    {
        var context = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext(
            """
            __builder.AddAttribute(0, "onclick", EventCallback.Factory.Create(this, HandleSave));
            __builder.AddAttribute(1, "class", "card");
            __builder.AddAttribute(2, "ErrorContent");
            __builder.AddAttribute(3, "Value", 42);
            __builder.AddAttribute(4, "OnSave", EventCallback.Factory.Create<string>(this, HandleChange));
            """,
            extraTypes: """
                public sealed class ExpressionBodyBoundary : ErrorBoundary
                {
                    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, "x");
                }
                """);
        var foreignContext = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext("""__builder.AddAttribute(0, dynamicAttributeName, HandleSave);""");
        var rootAnalysisType = typeof(BlazorErrorHandlingAnalyzer).GetNestedType("RootAnalysisState", System.Reflection.BindingFlags.NonPublic)!;
        var createElementRoot = rootAnalysisType.GetMethod("CreateElementRoot", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var createComponentRoot = rootAnalysisType.GetMethod("CreateComponentRoot", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var openComponent = rootAnalysisType.GetMethod("OpenComponent")!;
        var analyzeAttribute = rootAnalysisType.GetMethod("AnalyzeAttribute")!;
        var closeNode = rootAnalysisType.GetMethod("CloseNode")!;

        var errorBoundarySymbol = context.GetRequiredType("Microsoft.AspNetCore.Components.Web.ErrorBoundary");
        var expressionBodyBoundary = context.GetRequiredType("TestComponents.ExpressionBodyBoundary");
        var htmlAttribute = context.FindInvocation("AddAttribute", "\"onclick\"");
        var classAttribute = context.FindInvocation("AddAttribute", "\"class\"");
        var shortErrorContentAttribute = context.FindInvocation("AddAttribute", "\"ErrorContent\")");
        var intAttribute = context.FindInvocation("AddAttribute", "\"Value\", 42");
        var genericEventCallbackAttribute = context.FindInvocation("AddAttribute", "EventCallback.Factory.Create<string>");
        var foreignAttribute = foreignContext.FindInvocation("AddAttribute");

        var nullNameElementRoot = createElementRoot.Invoke(null, [Location.None, null])!;
        analyzeAttribute.Invoke(nullNameElementRoot, [htmlAttribute, context.SemanticModel, CancellationToken.None]);
        closeNode.Invoke(nullNameElementRoot, []);
        var nullNameRegions = (ImmutableArray<InteractiveRenderRegion>)rootAnalysisType.GetProperty("UncoveredRegions")!.GetValue(nullNameElementRoot)!;
        Assert.Single(nullNameRegions);
        Assert.Equal(string.Empty, nullNameRegions[0].RootName);

        var regularComponentRoot = createComponentRoot.Invoke(null, [null, errorBoundarySymbol, CancellationToken.None, Location.None])!;
        openComponent.Invoke(regularComponentRoot, [null, errorBoundarySymbol, Location.None]);
        closeNode.Invoke(regularComponentRoot, []);

        var ignoredComponentRoot = createComponentRoot.Invoke(null, [context.GetRequiredType("Microsoft.AspNetCore.Components.Web.PageTitle"), errorBoundarySymbol, CancellationToken.None, Location.None])!;
        openComponent.Invoke(ignoredComponentRoot, [null, errorBoundarySymbol, Location.None]);
        closeNode.Invoke(ignoredComponentRoot, []);

        Assert.False((bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("HasEventAttribute", shortErrorContentAttribute, context.SemanticModel, CancellationToken.None)!);
        Assert.True((bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("HasAttributeNamed", shortErrorContentAttribute, context.SemanticModel, CancellationToken.None, "ErrorContent")!);
        Assert.Null((string?)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("TryGetAttributeName", foreignAttribute, context.SemanticModel, CancellationToken.None));
        Assert.Null((InteractiveRenderRegionKind?)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("TryGetComponentInteractiveRegionKind", intAttribute, context.SemanticModel, CancellationToken.None));
        Assert.Equal(
            InteractiveRenderRegionKind.ComponentCallback,
            (InteractiveRenderRegionKind?)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer("TryGetComponentInteractiveRegionKind", genericEventCallbackAttribute, context.SemanticModel, CancellationToken.None));
        Assert.False((bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "IsBindingGeneratedComponentCallback",
            foreignAttribute,
            foreignAttribute.ArgumentList.Arguments[2].Expression,
            context.SemanticModel,
            CancellationToken.None)!);

        Assert.False((bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "BoundaryTypeHasBuiltInErrorContent",
            errorBoundarySymbol,
            errorBoundarySymbol,
            CancellationToken.None)!);
        Assert.False((bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "BoundaryTypeHasBuiltInErrorContent",
            expressionBodyBoundary,
            errorBoundarySymbol,
            CancellationToken.None)!);
    }

    private static InteractiveRenderRegionKind? GetInteractiveRegionKind(string statement)
    {
        var context = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext(statement);
        var invocation = context.FindInvocation("AddAttribute");
        return (InteractiveRenderRegionKind?)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "TryGetComponentInteractiveRegionKind",
            invocation,
            context.SemanticModel,
            CancellationToken.None);
    }

    private static bool IsBindingCallback(string statement)
    {
        var context = AnalyzerWhiteBoxTestHarness.CreateRenderTreeContext(statement);
        var invocation = context.FindInvocation("AddAttribute");
        var valueExpression = invocation.ArgumentList.Arguments.Count > 2
            ? invocation.ArgumentList.Arguments[2].Expression
            : invocation;
        return (bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "IsBindingGeneratedComponentCallback",
            invocation,
            valueExpression,
            context.SemanticModel,
            CancellationToken.None)!;
    }

    private static string? InvokeTryGetAttributeName(InvocationExpressionSyntax invocation, SemanticTestContext context) =>
        (string?)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "TryGetAttributeName",
            invocation,
            context.SemanticModel,
            CancellationToken.None);

    private static string? InvokeTryGetElementName(InvocationExpressionSyntax invocation, SemanticTestContext context) =>
        (string?)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "TryGetElementName",
            invocation,
            context.SemanticModel,
            CancellationToken.None);

    private static ITypeSymbol? InvokeGetValueExpressionType(ExpressionSyntax expression, SemanticTestContext context) =>
        (ITypeSymbol?)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "GetValueExpressionType",
            expression,
            context.SemanticModel,
            CancellationToken.None);

    private static bool InvokeIsCreateBinderInvocation(InvocationExpressionSyntax invocation, SemanticTestContext context) =>
        (bool)AnalyzerWhiteBoxTestHarness.InvokeAnalyzer(
            "IsCreateBinderInvocation",
            invocation,
            context.SemanticModel,
            CancellationToken.None)!;
}
