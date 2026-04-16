using Microsoft.CodeAnalysis;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class BlazorErrorHandlingAnalyzerTests
{
    [Fact]
    public async Task StaticComponent_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "Counter",
                renderTreeStatements: CreateButtonRenderTree("IncrementCount"),
                razorMethods: """
                    private void IncrementCount()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InteractiveComponent_DerivingFromErrorBoundary_StillReportsWhenFirstOpenIsNotBoundary()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "ProtectedCounter",
                renderTreeStatements: CreateButtonRenderTree("IncrementCount", elementName: "section"),
                razorMethods: """
                    private void IncrementCount()
                    {
                        currentCount++;
                    }
                    """,
                baseType: "global::Microsoft.AspNetCore.Components.Web.ErrorBoundary"));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id),
            diagnostic =>
            {
                Assert.Equal("NTBA0002", diagnostic.Id);
                Assert.Contains("IncrementCount", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task InteractiveComponent_WithErrorBoundaryRoot_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Counter",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(2, "button");
                        __builder2.AddAttribute(3, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, IncrementCount));
                        __builder2.CloseElement();
                    }));
                    __builder.AddAttribute(4, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(5, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void IncrementCount()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InteractiveComponent_WithCustomBoundaryRoot_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateCustomBoundary("CustomBoundary"),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Counter",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.CustomBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(2, "button");
                        __builder2.AddAttribute(3, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, IncrementCount));
                        __builder2.CloseElement();
                    }));
                    __builder.AddAttribute(4, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(5, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void IncrementCount()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InteractiveComponent_WithPageTitleBeforeBoundary_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Counter",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.PageTitle>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.AddContent(2, "Counter");
                    }));
                    __builder.CloseComponent();
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(3);
                    __builder.AddAttribute(4, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(5, "button");
                        __builder2.AddAttribute(6, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, IncrementCount));
                        __builder2.CloseElement();
                    }));
                    __builder.AddAttribute(7, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(8, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void IncrementCount()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Empty(diagnostics);
    }


    [Fact]
    public async Task InteractiveComponent_WithInertHtmlRootAroundBoundary_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "WrappedBoundary",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(1);
                    __builder.AddAttribute(2, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Counter>(3);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(4, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(5, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    __builder.CloseElement();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Counter",
                renderTreeStatements: """
                    __builder.OpenElement(0, "span");
                    __builder.CloseElement();
                    """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InteractiveComponent_WithEventCallbackHtmlRootBeforeBoundary_ReportsDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "HtmlRootWithCallback",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(2);
                    __builder.AddAttribute(3, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(4, "p");
                        __builder2.CloseElement();
                    }));
                    __builder.AddAttribute(5, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(6, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private void HandleClick()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id),
            diagnostic =>
            {
                Assert.Equal("NTBA0002", diagnostic.Id);
                Assert.Contains("HandleClick", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task InteractiveComponent_WithBoundaryRootThenUnprotectedComponentRoot_ReportsDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "MultipleRootComponent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Counter>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    __builder.OpenComponent<global::TestComponents.Counter>(5);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Counter",
                renderTreeStatements: """
                    __builder.OpenElement(0, "span");
                    __builder.CloseElement();
                    """));

        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Component 'MultipleRootComponent'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Component 'Counter'", StringComparison.Ordinal) && diagnostic.GetMessage().Contains("'MultipleRootComponent'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InteractiveComponent_WithBoundaryRootThenInertHtmlRoot_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "BoundaryAndInertHtml",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Counter>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    __builder.OpenElement(5, "div");
                    __builder.AddContent(6, "This is some text");
                    __builder.CloseElement();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Counter",
                renderTreeStatements: """
                    __builder.OpenElement(0, "span");
                    __builder.CloseElement();
                    """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InteractiveComponent_WithoutBoundary_ReportsComponentAndRootMethod()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Counter",
                renderTreeStatements: CreateButtonRenderTree("IncrementCount"),
                razorMethods: """
                    private void IncrementCount()
                    {
                        currentCount++;
                    }

                    private void IncrementSafely()
                    {
                        try
                        {
                            currentCount++;
                        }
                        catch (System.Exception)
                        {
                            throw;
                        }
                    }
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic =>
            {
                Assert.Equal("NTBA0001", diagnostic.Id);
                Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
            },
            diagnostic =>
            {
                Assert.Equal("NTBA0002", diagnostic.Id);
                Assert.Contains("IncrementCount", diagnostic.GetMessage(), StringComparison.Ordinal);
                Assert.Contains("Counter", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task InteractiveComponent_HelperCalledOnlyFromCaughtRoot_DoesNotReportMethodDiagnostic()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Counter",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        try
                        {
                            IncrementCore();
                        }
                        catch (System.Exception)
                        {
                            throw;
                        }
                    }

                    private void IncrementCore()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id));
    }

    [Fact]
    public async Task InteractiveComponent_RootDelegatingToCaughtMethod_DoesNotReportMethodDiagnostic()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Counter",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick() => HandleClickCore();

                    private void HandleClickCore()
                    {
                        try
                        {
                            currentCount++;
                        }
                        catch (System.Exception)
                        {
                            throw;
                        }
                    }
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id));
    }

    [Fact]
    public async Task InteractiveComponent_HelperCalledFromCaughtAndUncaughtRoots_ReportsHelperAndUncaughtRoot()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Counter",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleSafe));
                    __builder.CloseElement();
                    __builder.OpenElement(2, "button");
                    __builder.AddAttribute(3, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleUnsafe));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private void HandleSafe()
                    {
                        try
                        {
                            IncrementCore();
                        }
                        catch (System.Exception)
                        {
                            throw;
                        }
                    }

                    private void HandleUnsafe()
                    {
                        IncrementCore();
                    }

                    private void IncrementCore()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id),
            diagnostic =>
            {
                Assert.Equal("NTBA0002", diagnostic.Id);
                Assert.Contains("HandleUnsafe", diagnostic.GetMessage(), StringComparison.Ordinal);
            },
            diagnostic =>
            {
                Assert.Equal("NTBA0002", diagnostic.Id);
                Assert.Contains("IncrementCore", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task InteractiveComponent_WithoutBoundary_ReportsCodeBehindRootMethod()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "FetchData",
                renderTreeStatements: CreateButtonRenderTree("LoadAsync", elementName: "section")),
            TestComponentSources.CreateCodeBehind(
                componentName: "FetchData",
                methods: """
                    private async global::System.Threading.Tasks.Task LoadAsync()
                    {
                        await global::System.Threading.Tasks.Task.CompletedTask;
                    }
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id),
            diagnostic =>
            {
                Assert.Equal("NTBA0002", diagnostic.Id);
                Assert.Contains("LoadAsync", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task InteractiveComponent_ExpressionBodiedRootMethod_ReportsMissingTryCatch()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Counter",
                renderTreeStatements: CreateButtonRenderTree("IncrementCount"),
                razorMethods: """
                    private void IncrementCount() => currentCount++;
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id),
            diagnostic =>
            {
                Assert.Equal("NTBA0002", diagnostic.Id);
                Assert.Contains("IncrementCount", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task InteractiveChild_UsedOnlyByBoundaryCoveredParent_DoesNotReportDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Parent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Child>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Child",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task InteractiveChild_WithMixedCoveredAndUncoveredOwners_StillReportsDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "CoveredParent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Child>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "UncoveredParent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.OpenElement(1, "button");
                    __builder.AddAttribute(2, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleParent));
                    __builder.CloseElement();
                    __builder.OpenComponent<global::TestComponents.Child>(3);
                    __builder.CloseComponent();
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private void HandleParent()
                    {
                        currentCount++;
                    }
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Child",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Equal(4, diagnostics.Count);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("UncoveredParent", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Child", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("UncoveredParent", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("Child", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InteractiveCoverage_FlowsTransitivelyDownComponentHierarchy()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "GrandParent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Parent>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Parent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.Child>(0);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Child",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Empty(diagnostics);
    }


    [Fact]
    public async Task InteractiveChild_WrappedByStaticBoundaryParent_StillReportsDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "StaticParent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Child>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Child",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id),
            diagnostic =>
            {
                Assert.Equal("NTBA0002", diagnostic.Id);
                Assert.Contains("Child", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task InteractiveChild_WrappedByStaticAndInteractiveBoundaryOwners_StillReportsDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "StaticParent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Child>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "InteractiveParent",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Child>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Child",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        currentCount++;
                    }
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id),
            diagnostic =>
            {
                Assert.Equal("NTBA0002", diagnostic.Id);
                Assert.Contains("Child", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task InteractiveHierarchy_SharedRenderMode_WarnsOnAllLevels_AndTopBoundaryCoversWholeTree()
    {
        var unwrappedDiagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.Child>(0);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Child",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.Grandchild>(0);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Grandchild",
                renderTreeStatements: """
                    __builder.OpenElement(0, "span");
                    __builder.CloseElement();
                    """));

        Assert.Equal(3, unwrappedDiagnostics.Count);
        Assert.Contains(unwrappedDiagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Component 'Page'", StringComparison.Ordinal));
        Assert.Contains(unwrappedDiagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Component 'Child'", StringComparison.Ordinal) && diagnostic.GetMessage().Contains("'Page'", StringComparison.Ordinal));
        Assert.Contains(unwrappedDiagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Component 'Grandchild'", StringComparison.Ordinal) && diagnostic.GetMessage().Contains("'Page'", StringComparison.Ordinal));

        var wrappedDiagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Child>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Child",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.Grandchild>(0);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Grandchild",
                renderTreeStatements: """
                    __builder.OpenElement(0, "span");
                    __builder.CloseElement();
                    """));

        Assert.Empty(wrappedDiagnostics);
    }

    [Fact]
    public async Task StaticPage_WithInteractiveDescendants_WarnsOnAllLevels_AndCoverageStopsAtRenderModeBoundary()
    {
        var unwrappedDiagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.Child>(0);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Child",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.Grandchild>(0);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Grandchild",
                renderTreeStatements: """
                    __builder.OpenElement(0, "span");
                    __builder.CloseElement();
                    """));

        Assert.Equal(3, unwrappedDiagnostics.Count);
        Assert.Contains(unwrappedDiagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Component 'Page'", StringComparison.Ordinal) && diagnostic.GetMessage().Contains("'Page'", StringComparison.Ordinal));
        Assert.Contains(unwrappedDiagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Component 'Child'", StringComparison.Ordinal) && diagnostic.GetMessage().Contains("'Child'", StringComparison.Ordinal));
        Assert.Contains(unwrappedDiagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Component 'Grandchild'", StringComparison.Ordinal) && diagnostic.GetMessage().Contains("'Child'", StringComparison.Ordinal));

        var pageWrappedDiagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Child>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Child",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.Grandchild>(0);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Grandchild",
                renderTreeStatements: """
                    __builder.OpenElement(0, "span");
                    __builder.CloseElement();
                    """));

        Assert.Equal(2, pageWrappedDiagnostics.Count);
        Assert.DoesNotContain(pageWrappedDiagnostics, diagnostic => diagnostic.GetMessage().Contains("Component 'Page'", StringComparison.Ordinal));
        Assert.Contains(pageWrappedDiagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Component 'Child'", StringComparison.Ordinal) && diagnostic.GetMessage().Contains("'Child'", StringComparison.Ordinal));
        Assert.Contains(pageWrappedDiagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("Component 'Grandchild'", StringComparison.Ordinal) && diagnostic.GetMessage().Contains("'Child'", StringComparison.Ordinal));

        var childWrappedDiagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.Child>(0);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Child",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.Grandchild>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Grandchild",
                renderTreeStatements: """
                    __builder.OpenElement(0, "span");
                    __builder.CloseElement();
                    """));

        Assert.Collection(
            childWrappedDiagnostics,
            diagnostic =>
            {
                Assert.Equal("NTBA0001", diagnostic.Id);
                Assert.Contains("Component 'Page'", diagnostic.GetMessage(), StringComparison.Ordinal);
                Assert.Contains("'Page'", diagnostic.GetMessage(), StringComparison.Ordinal);
            });
    }

    private static string CreateButtonRenderTree(string handlerName, string elementName = "button") =>
        $$"""
        __builder.OpenElement(0, "{{elementName}}");
        __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, {{handlerName}}));
        __builder.CloseElement();
        """;
}
