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
    public async Task InteractiveComponent_WithOnlyIgnoredHeadContentAndStaticMarkup_DoesNotReportNtba0001()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Forbidden",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.PageTitle>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.AddContent(2, "Forbidden");
                    }));
                    __builder.CloseComponent();
                    __builder.OpenElement(3, "h1");
                    __builder.AddContent(4, "Forbidden");
                    __builder.CloseElement();
                    __builder.OpenElement(5, "p");
                    __builder.AddContent(6, "Static content");
                    __builder.CloseElement();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task InteractiveParent_DoesNotReportNtba0001_ForStaticLeafChildWithOnlyIgnoredHeadContentAndMarkup()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.PageHeader>(0);
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "PageHeader",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.PageTitle>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.AddContent(2, "Header");
                    }));
                    __builder.CloseComponent();
                    __builder.OpenElement(3, "div");
                    __builder.OpenElement(4, "h3");
                    __builder.AddContent(5, "Header");
                    __builder.CloseElement();
                    __builder.CloseElement();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task InteractiveComponent_WithSafeChildComponentBeforeBoundary_DoesNotReportNtba0001()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.PageHeader>(0);
                    __builder.AddAttribute(1, "Title", "Header");
                    __builder.CloseComponent();
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(2);
                    __builder.AddAttribute(3, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(4, "div");
                        __builder2.CloseElement();
                    }));
                    __builder.AddAttribute(5, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(6, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "PageHeader",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task InteractiveComponent_WithChildComponentCallbackBeforeBoundary_ReportsNtba0001()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.CallbackChild>(0);
                    __builder.AddAttribute(1, "OnSave", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, HandleSave));
                    __builder.CloseComponent();
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(2);
                    __builder.AddAttribute(3, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(4, "div");
                        __builder2.CloseElement();
                    }));
                    __builder.AddAttribute(5, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(6, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void HandleSave()
                    {
                        currentCount++;
                    }
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "CallbackChild",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("component 'Page'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InteractiveComponent_WithChildComponentRenderFragmentOnlyBeforeBoundary_DoesNotReportNtba0001()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.TemplateChild>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(2, "span");
                        __builder2.CloseElement();
                    }));
                    __builder.CloseComponent();
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(3);
                    __builder.AddAttribute(4, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(5, "div");
                        __builder2.CloseElement();
                    }));
                    __builder.AddAttribute(6, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(7, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "TemplateChild",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task InteractiveComponent_WithChildComponentBindingCallbackBeforeBoundary_ReportsNtba0001()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.BindChild>(0);
                    __builder.AddAttribute(1, "Value", currentValue);
                    __builder.AddAttribute(2, "ValueChanged", global::Microsoft.AspNetCore.Components.EventCallback.Factory.CreateBinder<string>(this, __value => currentValue = __value, currentValue));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private string currentValue = string.Empty;
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "BindChild",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("component 'Page'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InteractiveComponent_WithDelegateAndAnonymousComponentCallbacksBeforeBoundary_ReportsMultipleNtba0001Diagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::TestComponents.DelegateChild>(0);
                    __builder.AddAttribute(1, "Changed", (global::System.Action)(() => HandleLambda()));
                    __builder.CloseComponent();
                    __builder.OpenComponent<global::TestComponents.AnonymousChild>(2);
                    __builder.AddAttribute(3, "Changed", (global::System.Action)delegate { HandleAnonymous(); });
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void HandleLambda()
                    {
                        currentCount++;
                    }

                    private void HandleAnonymous()
                    {
                        currentCount++;
                    }
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "DelegateChild",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "AnonymousChild",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """));

        Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.Id == "NTBA0001"));
    }

    [Fact]
    public async Task InteractiveComponent_WithIfElseRoots_RecognizesBoundaryInProtectedBranch()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Edit",
                renderTreeStatements: """
                    if (global::System.DateTime.Now.Ticks > 0)
                    {
                        __builder.OpenComponent<global::TestComponents.PageHeader>(0);
                        __builder.AddAttribute(1, "Title", "Edit Contact");
                        __builder.CloseComponent();
                        __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(2);
                        __builder.AddAttribute(3, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                        {
                            __builder2.OpenComponent<global::TestComponents.EditContactForm>(4);
                            __builder2.CloseComponent();
                        }));
                        __builder.AddAttribute(5, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                        {
                            __builder3.OpenElement(6, "p");
                            __builder3.CloseElement();
                        }));
                        __builder.CloseComponent();
                    }
                    else
                    {
                        __builder.OpenComponent<global::TestComponents.PageHeader>(7);
                        __builder.AddAttribute(8, "Title", "Contact Not Found");
                        __builder.CloseComponent();
                        __builder.OpenElement(9, "p");
                        __builder.AddContent(10, "The provided contact could not be found.");
                        __builder.CloseElement();
                    }
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "PageHeader",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "EditContactForm",
                renderTreeStatements: """
                    __builder.OpenElement(0, "form");
                    __builder.CloseElement();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("component 'Edit'", StringComparison.Ordinal));
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
    public async Task InteractiveComponent_WithBoundaryRootThenSafeComponentRoot_DoesNotReportDiagnostics()
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

        Assert.Empty(diagnostics);
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

        var missingBoundaryDiagnostics = diagnostics.Where(diagnostic => diagnostic.Id == "NTBA0001").ToArray();

        Assert.Equal(4, diagnostics.Count);
        Assert.Equal(2, missingBoundaryDiagnostics.Length);
        Assert.NotEqual(
            missingBoundaryDiagnostics[0].Location.GetLineSpan().StartLinePosition.Line,
            missingBoundaryDiagnostics[1].Location.GetLineSpan().StartLinePosition.Line);
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("HandleUnsafe", StringComparison.Ordinal));
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("IncrementCore", StringComparison.Ordinal));
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
    public async Task InteractiveHierarchy_SharedRenderMode_ReportsChildrenButResolvesToTopLevelOwner_AndTopBoundaryCoversWholeTree()
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

        Assert.Empty(unwrappedDiagnostics);

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
    public async Task StaticPage_WithInteractiveDescendants_DoesNotWarnOnStaticLeafDescendants_AndCoverageStopsAtRenderModeBoundary()
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

        Assert.Empty(unwrappedDiagnostics);

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

        Assert.Empty(pageWrappedDiagnostics);

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

        Assert.Empty(childWrappedDiagnostics);
    }

    [Fact]
    public async Task InteractiveBoundaryWrappedComponent_ProtectsChildrenRenderedFromHelperMethod()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Page",
                renderTreeStatements: """
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        RenderPageContent(__builder2);
                    }));
                    __builder.AddAttribute(2, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(3, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    private void RenderPageContent(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                    {
                        __builder.OpenComponent<global::TestComponents.Child>(0);
                        __builder.CloseComponent();
                    }
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Child",
                renderTreeStatements: """
                    __builder.OpenElement(0, "span");
                    __builder.CloseElement();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task InteractiveBoundaryWrappedDerivedComponent_ProtectsGenericBaseBuildRenderTreeChildren()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            new SourceFile(
                Path: "Components/BasePage.razor.g.cs",
                Text: """
                    namespace TestComponents
                    {
                        public partial class BasePage<TPage> : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
                                __builder.OpenComponent<global::TestComponents.Child>(0);
                                __builder.CloseComponent();
                            }
                        }
                    }
                    """),
            new SourceFile(
                Path: "Components/Page.razor.g.cs",
                Text: """
                    namespace TestComponents
                    {
                        [Page.__PrivateComponentRenderModeAttribute]
                        public partial class Page : global::TestComponents.BasePage<Page>
                        {
                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
                                __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                                __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                                {
                                    base.BuildRenderTree(__builder2);
                                }));
                                __builder.AddAttribute(2, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                                {
                                    __builder3.OpenElement(3, "p");
                                    __builder3.CloseElement();
                                }));
                                __builder.CloseComponent();
                            }

                            private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                            {
                                public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode =>
                                    global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer;
                            }
                        }
                    }
                    """),
            TestComponentSources.CreateStaticComponent(
                componentName: "Child",
                renderTreeStatements: """
                    __builder.OpenElement(0, "span");
                    __builder.CloseElement();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    private static string CreateButtonRenderTree(string handlerName, string elementName = "button") =>
        $$"""
        __builder.OpenElement(0, "{{elementName}}");
        __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, {{handlerName}}));
        __builder.CloseElement();
        """;
}
