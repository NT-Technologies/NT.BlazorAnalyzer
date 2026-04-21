using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class BlazorCoverageRegressionTests
{
    [Fact]
    public async Task InteractiveComponent_DeclarationOnlyPartialMethod_DoesNotAffectInteractiveRootDiagnostics()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "PartialMethodComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        throw new global::System.InvalidOperationException();
                    }

                    partial void HelperDeclarationOnly();
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
    public async Task LifecycleMethod_WithOnlyNestedLocalFunction_DoesNotReportNtba0003()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "NestedLifecycleComponent",
                renderTreeStatements: """
                    var sequence = 0;
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(sequence++);
                    __builder.AddAttribute(sequence++, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenElement(sequence++, "div");
                        __builder2.CloseElement();
                    }));
                    __builder.AddAttribute(sequence++, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(sequence++, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """,
                razorMethods: """
                    protected override void OnParametersSet()
                    {
                        void Nested()
                        {
                            throw new global::System.InvalidOperationException();
                        }
                    }
                    """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task JsObjectReferenceDisposeAsync_ReportsNtba0004AndNtba0005()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "DisposeInteropComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSObjectReference Module => default!;

                    public async global::System.Threading.Tasks.ValueTask DisposeAsync()
                    {
                        await Module.DisposeAsync();
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0004");
        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0005");
    }

    [Fact]
    public async Task CatchCallingNonLoggingMethod_StillReportsNtba0008()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "ConsoleCatchComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        try
                        {
                            ThrowNow();
                        }
                        catch (global::System.Exception ex)
                        {
                            global::System.Console.WriteLine(ex.Message);
                        }
                    }

                    private void ThrowNow()
                    {
                        throw new global::System.InvalidOperationException();
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }

    [Fact]
    public async Task CatchCallingILoggerMember_DoesNotReportNtba0008()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "LoggerCatchComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private global::Microsoft.Extensions.Logging.ILogger Logger => default!;

                    private void HandleClick()
                    {
                        try
                        {
                            ThrowNow();
                        }
                        catch (global::System.Exception)
                        {
                            Logger.BeginScope("scope");
                        }
                    }

                    private void ThrowNow()
                    {
                        throw new global::System.InvalidOperationException();
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }

    [Fact]
    public async Task JsInteropInsideUnrelatedIfGuard_StillReportsNtba0006()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "UnrelatedGuardComponent",
                renderTreeStatements: """
                    __builder.OpenElement(0, "div");
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private global::Microsoft.JSInterop.IJSRuntime JS => default!;
                    private bool ready = true;

                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                    {
                        if (ready)
                        {
                            try
                            {
                                await JS.InvokeVoidAsync("doSomething");
                            }
                            catch (global::System.Exception)
                            {
                                throw;
                            }
                        }
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0006");
    }

    [Fact]
    public async Task TryFinallyWithoutCatch_ReportsNtba0002_WhenTheRootDoesFailureProneWork()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "TryFinallyComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        try
                        {
                            global::System.Console.WriteLine(currentCount);
                        }
                        finally
                        {
                            currentCount--;
                        }
                    }
                    """));

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0002" && diagnostic.GetMessage().Contains("HandleClick", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecursiveDelegation_WithoutFailureProneWork_DoesNotReportNtba0002()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "RecursiveDelegationComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick() => HandleClickCore();

                    private void HandleClickCore() => HandleClick();
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0001", diagnostic.Id));
    }

    [Fact]
    public async Task NonGenericOpenComponentBoundary_WithoutCallbacks_DoesNotReportNtba0001()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "NonGenericBoundaryComponent",
                renderTreeStatements: """
                    __builder.OpenComponent(0, typeof(global::Microsoft.AspNetCore.Components.Web.ErrorBoundary));
                    __builder.CloseComponent();
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task BoundaryRootWithDirectNestedElement_IsRecognized()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "NestedBoundaryRootComponent",
                renderTreeStatements: """
                    var sequence = 0;
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(sequence++);
                    __builder.OpenElement(sequence++, "section");
                    __builder.CloseElement();
                    __builder.CloseComponent();
                    """));

        Assert.Collection(
            diagnostics,
            diagnostic => Assert.Equal("NTBA0009", diagnostic.Id));
    }

    [Fact]
    public async Task MissingErrorBoundary_UsesRazorMarkupLocation_WhenRazorSourceIsAvailable()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "MarkupCounter",
                    renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                    razorMethods: """
                        private void HandleClick()
                        {
                            currentCount++;
                        }
                        """)
            ],
            additionalFiles:
            [
                TestComponentSources.CreateRazorMarkup(
                    componentName: "MarkupCounter",
                    markup: """
                        @rendermode InteractiveServer
                        <button @onclick="HandleClick">Click</button>
                        """)
            ]);

        var diagnostic = Assert.Single(diagnostics, static item => item.Id == "NTBA0001");
        Assert.EndsWith("Components/MarkupCounter.razor", diagnostic.Location.GetLineSpan().Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingErrorBoundary_ForComponentMethodGroupCallback_UsesRazorComponentLocation()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "EditPage",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::TestComponents.EditorForm>(0);
                        __builder.AddAttribute(1, "OnSave", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create(this, HandleSave));
                        __builder.CloseComponent();
                        """,
                    razorMethods: """
                        private void HandleSave()
                        {
                            currentCount++;
                        }
                        """),
                TestComponentSources.CreateStaticComponent(
                    componentName: "EditorForm",
                    renderTreeStatements: """
                        __builder.OpenElement(0, "form");
                        __builder.CloseElement();
                        """)
            ],
            additionalFiles:
            [
                TestComponentSources.CreateRazorMarkup(
                    componentName: "EditPage",
                    markup: """
                        @rendermode InteractiveServer
                        <EditorForm OnSave="HandleSave" />
                        """)
            ]);

        var diagnostic = Assert.Single(diagnostics, static item => item.Id == "NTBA0001");
        Assert.EndsWith("Components/EditPage.razor", diagnostic.Location.GetLineSpan().Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }

    [Fact]
    public async Task MissingErrorBoundary_UsesSyntheticRazorLocation_WhenOnlyGeneratedRazorExists()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "GeneratedOnlyCounter",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        currentCount++;
                    }
                    """));

        var diagnostic = Assert.Single(diagnostics, static item => item.Id == "NTBA0001");
        Assert.EndsWith("Components/GeneratedOnlyCounter.razor.g.cs", diagnostic.Location.GetLineSpan().Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SafeChildComponentBeforeBoundary_InRazorMarkup_DoesNotReportNtba0001()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "EditPage",
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
                        """)
            ],
            additionalFiles:
            [
                TestComponentSources.CreateRazorMarkup(
                    componentName: "EditPage",
                    markup: """
                        @rendermode InteractiveServer
                        <PageHeader Title="Header" />
                        <ErrorBoundary>
                            <div></div>
                            <ErrorContent>
                                <p>Error</p>
                            </ErrorContent>
                        </ErrorBoundary>
                        """)
            ]);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task SafeChildComponentWithComputedParameterBeforeBoundary_InRazorMarkup_DoesNotReportNtba0001()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "EditPage",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::TestComponents.PageHeader>(0);
                        __builder.AddAttribute(1, "Title", $"Edit {name}");
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
                        private string name = "Alice";
                        """),
                TestComponentSources.CreateStaticComponent(
                    componentName: "PageHeader",
                    renderTreeStatements: """
                        __builder.OpenElement(0, "div");
                        __builder.CloseElement();
                        """)
            ],
            additionalFiles:
            [
                TestComponentSources.CreateRazorMarkup(
                    componentName: "EditPage",
                    markup: """
                        @rendermode InteractiveServer
                        <PageHeader Title="@($"Edit {name}")" />
                        <ErrorBoundary>
                            <div></div>
                            <ErrorContent>
                                <p>Error</p>
                            </ErrorContent>
                        </ErrorBoundary>
                        """)
            ]);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task MissingErrorContent_UsesRazorMarkupLocation_WhenRazorSourceIsAvailable()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "BoundaryMarkupCounter",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                        __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                        {
                            __builder2.OpenElement(2, "button");
                            __builder2.AddAttribute(3, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                            __builder2.CloseElement();
                        }));
                        __builder.CloseComponent();
                        """,
                    razorMethods: """
                        private void HandleClick()
                        {
                            currentCount++;
                        }
                        """)
            ],
            additionalFiles:
            [
                TestComponentSources.CreateRazorMarkup(
                    componentName: "BoundaryMarkupCounter",
                    markup: """
                        @rendermode InteractiveServer
                        <ErrorBoundary>
                            <button @onclick="HandleClick">Click</button>
                        </ErrorBoundary>
                        """)
            ]);

        var diagnostic = Assert.Single(diagnostics, static item => item.Id == "NTBA0009");
        Assert.EndsWith("Components/BoundaryMarkupCounter.razor", diagnostic.Location.GetLineSpan().Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LayoutBoundary_ReportsNtba0010()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                TestComponentSources.CreateStaticComponent(
                    componentName: "MainLayout",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                        __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                        {
                            __builder2.AddContent(2, Body);
                        }));
                        __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                        {
                            __builder3.OpenElement(4, "p");
                            __builder3.CloseElement();
                        }));
                        __builder.CloseComponent();
                        """,
                    baseType: "global::Microsoft.AspNetCore.Components.LayoutComponentBase")
            ],
            additionalFiles:
            [
                TestComponentSources.CreateRazorMarkup(
                    componentName: "MainLayout",
                    markup: """
                        @inherits LayoutComponentBase
                        <ErrorBoundary>
                            @Body
                            <ErrorContent>
                                <p>Error</p>
                            </ErrorContent>
                        </ErrorBoundary>
                        """)
            ]);

        var diagnostic = Assert.Single(diagnostics, static item => item.Id == "NTBA0010");
        Assert.Contains("Prefer page/widget boundaries", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Components/MainLayout.razor", diagnostic.Location.GetLineSpan().Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LayoutBoundaryWithRouteKey_StillReportsNtba0010()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                TestComponentSources.CreateStaticComponent(
                    componentName: "MainLayout",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                        __builder.SetKey(CurrentRoute);
                        __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                        {
                            __builder2.AddContent(2, Body);
                        }));
                        __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                        {
                            __builder3.OpenElement(4, "p");
                            __builder3.CloseElement();
                        }));
                        __builder.CloseComponent();
                        """,
                    razorMethods: """
                        private string CurrentRoute => "/claims";
                        """,
                    baseType: "global::Microsoft.AspNetCore.Components.LayoutComponentBase")
            ],
            additionalFiles:
            [
                TestComponentSources.CreateRazorMarkup(
                    componentName: "MainLayout",
                    markup: """
                        @inherits LayoutComponentBase
                        <ErrorBoundary @key="CurrentRoute">
                            @Body
                            <ErrorContent>
                                <p>Error</p>
                            </ErrorContent>
                        </ErrorBoundary>
                        """)
            ]);

        Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "NTBA0010");
    }

    [Fact]
    public async Task LayoutBoundaryWithSnapshotRouteKey_ReportsNtba0010()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                TestComponentSources.CreateStaticComponent(
                    componentName: "MainLayout",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                        __builder.SetKey(_currentRoute);
                        __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                        {
                            __builder2.AddContent(2, Body);
                        }));
                        __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                        {
                            __builder3.OpenElement(4, "p");
                            __builder3.CloseElement();
                        }));
                        __builder.CloseComponent();
                        """,
                    razorMethods: """
                        private string _currentRoute = default!;
                        private global::Microsoft.AspNetCore.Components.NavigationManager _navManager = default!;

                        protected override void OnInitialized()
                        {
                            _currentRoute = _navManager.Uri;
                        }
                        """,
                    baseType: "global::Microsoft.AspNetCore.Components.LayoutComponentBase")
            ],
            additionalFiles:
            [
                TestComponentSources.CreateRazorMarkup(
                    componentName: "MainLayout",
                    markup: """
                        @inherits LayoutComponentBase
                        @inject NavigationManager _navManager

                        <ErrorBoundary @key="_currentRoute">
                            @Body
                            <ErrorContent>
                                <p>Error</p>
                            </ErrorContent>
                        </ErrorBoundary>

                        @code {
                            private string _currentRoute = default!;

                            protected override void OnInitialized()
                            {
                                _currentRoute = _navManager.Uri;
                            }
                        }
                        """)
            ]);

        var diagnostic = Assert.Single(diagnostics, static item => item.Id == "NTBA0010");
        Assert.Contains("Prefer page/widget boundaries", diagnostic.GetMessage(), StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Components/MainLayout.razor", diagnostic.Location.GetLineSpan().Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InteractiveLayoutWithoutBoundary_DoesNotReportNtba0001()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "MainLayout",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    """,
                razorMethods: """
                    private void HandleClick()
                    {
                    }
                    """,
                baseType: "global::Microsoft.AspNetCore.Components.LayoutComponentBase"));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001");
    }

    [Fact]
    public async Task LayoutBoundaryWithoutRouteKey_UsesRazorMarkupLocation_ForSourceGeneratorPath()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                new SourceFile(
                    Path: @"obj\Debug\net9.0\Microsoft.CodeAnalysis.Razor.Compiler\Microsoft.NET.Sdk.Razor.SourceGenerators.RazorSourceGenerator\Layout\MainLayout_razor.g.cs",
                    Text: """
                        namespace TestComponents
                        {
                            public partial class MainLayout : global::Microsoft.AspNetCore.Components.LayoutComponentBase
                            {
                                protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                                {
                                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                                    {
                                        __builder2.AddContent(2, Body);
                                    }));
                                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                                    {
                                        __builder3.OpenElement(4, "p");
                                        __builder3.CloseElement();
                                    }));
                                    __builder.CloseComponent();
                                }
                            }
                        }
                        """)
            ],
            additionalFiles:
            [
                TestComponentSources.CreateRazorMarkup(
                    componentName: @"Layout\MainLayout",
                    markup: """
                        @inherits LayoutComponentBase
                        <ErrorBoundary>
                            @Body
                            <ErrorContent>
                                <p>Error</p>
                            </ErrorContent>
                        </ErrorBoundary>
                        """)
            ]);

        var diagnostic = Assert.Single(diagnostics, static item => item.Id == "NTBA0010");
        Assert.EndsWith(@"Layout\MainLayout.razor", diagnostic.Location.GetLineSpan().Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DerivedBoundaryRoot_WithProtectedConditionalContent_DoesNotReportNtba0001_ForOwnerOrInteractiveChild()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                TestComponentSources.CreateCustomBoundary("IntersectErrorBoundary"),
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "PaginationButtonsWithCount",
                    renderTreeStatements: """
                        __builder.OpenElement(0, "div");
                        __builder.CloseElement();
                        """),
                new SourceFile(
                    Path: "Components/DashboardItem.cs",
                    Text: """
                        namespace TestComponents;

                        public abstract class DashboardItem : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            protected static global::Microsoft.AspNetCore.Components.RenderFragment RenderTitle(string title) => __builder =>
                            {
                                __builder.OpenElement(0, "h3");
                                __builder.AddContent(1, title);
                                __builder.CloseElement();
                            };
                        }
                        """),
                new SourceFile(
                    Path: "Components/TnTDataGrid.razor.g.cs",
                    Text: """
                        namespace TestComponents
                        {
                            public partial class TnTDataGrid : global::Microsoft.AspNetCore.Components.ComponentBase
                            {
                                [global::Microsoft.AspNetCore.Components.Parameter]
                                public global::System.Func<int, int>? SortBy { get; set; }

                                protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                                {
                                    __builder.OpenElement(0, "div");
                                    __builder.CloseElement();
                                }
                            }
                        }
                        """),
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "UpcomingAndPastDueFollowUp",
                    baseType: "global::TestComponents.DashboardItem",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::TestComponents.IntersectErrorBoundary>(0);
                        __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                        {
                            if (!RendererInfo.IsInteractive)
                            {
                                __builder2.OpenElement(2, "div");
                                __builder2.CloseElement();
                            }
                            else
                            {
                                __builder2.AddContent(3, RenderTitle("Upcoming and Past Due Follow-ups"));
                                __builder2.OpenComponent<global::TestComponents.TnTDataGrid>(4);
                                __builder2.AddAttribute(5, "SortBy", (global::System.Func<int, int>)(p => p));
                                __builder2.CloseComponent();
                                __builder2.OpenComponent<global::TestComponents.PaginationButtonsWithCount>(6);
                                __builder2.CloseComponent();
                            }
                        }));
                        __builder.CloseComponent();
                        """,
                    razorMethods: """
                        private RenderInfo RendererInfo { get; } = new RenderInfo();

                        private sealed class RenderInfo
                        {
                            public bool IsInteractive { get; } = true;
                        }
                        """)
            ],
            additionalFiles:
            [
                TestComponentSources.CreateRazorMarkup(
                    componentName: "UpcomingAndPastDueFollowUp",
                    markup: """
                        @rendermode InteractiveServer
                        @inherits DashboardItem

                        <TestComponents.IntersectErrorBoundary>
                            @if (!RendererInfo.IsInteractive) {
                                <div></div>
                            }
                            else {
                                @RenderTitle("Upcoming and Past Due Follow-ups")
                                <TnTDataGrid SortBy="@(p => p)" />
                                <PaginationButtonsWithCount />
                            }
                        </TestComponents.IntersectErrorBoundary>
                        """)
            ]);

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("UpcomingAndPastDueFollowUp", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0001" && diagnostic.GetMessage().Contains("PaginationButtonsWithCount", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SharedRenderModeCoverage_WorksAcrossDifferentModePropertySyntax()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            new SourceFile(
                Path: "Components/Page.razor.g.cs",
                Text: """
                    namespace TestComponents
                    {
                        [TestComponents.Page.__PrivateComponentRenderModeAttribute]
                        public partial class Page : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
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
                            }

                            private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                            {
                                public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode
                                {
                                    get => global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveAuto;
                                }
                            }
                        }
                    }
                    """),
            new SourceFile(
                Path: "Components/Child.razor.g.cs",
                Text: """
                    namespace TestComponents
                    {
                        [TestComponents.Child.__PrivateComponentRenderModeAttribute]
                        public partial class Child : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
                                __builder.OpenElement(0, "button");
                                __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                                __builder.CloseElement();
                            }

                    #line 100 "Components/Child.razor"
                            private void HandleClick()
                            {
                                currentCount++;
                            }
                    #line default
                    #line hidden

                            private sealed class __PrivateComponentRenderModeAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                            {
                                public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode
                                {
                                    get
                                    {
                                        return global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveAuto;
                                    }
                                }
                            }
                        }
                    }
                    """));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CatchCallingTelemetryHelperByIdentifier_DoesNotReportNtba0008()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "TelemetryCatchComponent",
                renderTreeStatements: CreateButtonRenderTree("HandleClick"),
                razorMethods: """
                    private void HandleClick()
                    {
                        try
                        {
                            ThrowNow();
                        }
                        catch (global::System.Exception ex)
                        {
                            CaptureException(ex);
                        }
                    }

                    private void ThrowNow()
                    {
                        throw new global::System.InvalidOperationException();
                    }

                    private void CaptureException(global::System.Exception ex)
                    {
                    }
                    """));

        Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "NTBA0008");
    }

    private static string CreateButtonRenderTree(string handlerName) =>
        $$"""
        __builder.OpenElement(0, "button");
        __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, {{handlerName}}));
        __builder.CloseElement();
        """;
}
