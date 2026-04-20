using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class BlazorErrorHandlingCodeFixProviderTests
{
    [Fact]
    public void Provider_AdvertisesSupportedDiagnostics_AndBatchFixAll()
    {
        var provider = new BlazorErrorHandlingCodeFixProvider();

        Assert.Equal(
            ["NTBA0003", "NTBA0004", "NTBA0005", "NTBA0006", "NTBA0007", "NTBA0008", "NTBA0009"],
            provider.FixableDiagnosticIds.OrderBy(static id => id, StringComparer.Ordinal).ToArray());
        Assert.Same(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    [Fact]
    public async Task LifecycleDiagnostic_OffersTryCatchFix()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                protected override void OnInitialized()
                {
                    DoWork();
                }

                private void DoWork()
                {
                }
            }
            """;

        var spanStart = source.IndexOf("OnInitialized", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0003", "Components/Counter.razor.cs", spanStart, "OnInitialized".Length);

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Wrap body in try/catch");

        Assert.Contains("try", updatedSource, StringComparison.Ordinal);
        Assert.Contains("catch (global::System.Exception ex)", updatedSource, StringComparison.Ordinal);
        Assert.Contains("throw;", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeDiagnostic_OffersTryCatchFix()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                public void Dispose()
                {
                    Cleanup();
                }

                private void Cleanup()
                {
                }
            }
            """;

        var spanStart = source.IndexOf("Dispose", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0004", "Components/Counter.razor.cs", spanStart, "Dispose".Length);

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Wrap body in try/catch");

        Assert.Contains("catch (global::System.Exception ex)", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsInteropDiagnostic_OffersTryCatchFix()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                private async global::System.Threading.Tasks.Task HandleClick()
                {
                    await JS.InvokeVoidAsync("doSomething");
                }
            }
            """;

        var spanStart = source.IndexOf("HandleClick", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0005", "Components/Counter.razor.cs", spanStart, "HandleClick".Length);

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Wrap body in try/catch");

        Assert.Contains("await JS.InvokeVoidAsync(\"doSomething\");", updatedSource, StringComparison.Ordinal);
        Assert.Contains("catch (global::System.Exception ex)", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsInteropDiagnostic_OffersTryCatchFix_ForExpressionBodiedMethod()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                private async global::System.Threading.Tasks.Task HandleClick() => await JS.InvokeVoidAsync("doSomething");
            }
            """;

        var spanStart = source.IndexOf("HandleClick", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0005", "Components/Counter.razor.cs", spanStart, "HandleClick".Length);

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Wrap body in try/catch");

        Assert.Contains("private async global::System.Threading.Tasks.Task HandleClick()", updatedSource, StringComparison.Ordinal);
        Assert.Contains("try", updatedSource, StringComparison.Ordinal);
        Assert.Contains("await JS.InvokeVoidAsync(\"doSomething\");", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractivityGuardDiagnostic_OffersGuardFix()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                {
                    await JS.InvokeVoidAsync("doSomething");
                }
            }
            """;

        var spanStart = source.IndexOf("OnInitializedAsync", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0006", "Components/Counter.razor.cs", spanStart, "OnInitializedAsync".Length);

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Guard with RendererInfo.IsInteractive");

        Assert.Contains("if (RendererInfo.IsInteractive)", updatedSource, StringComparison.Ordinal);
        Assert.Contains("await JS.InvokeVoidAsync(\"doSomething\");", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractivityGuardDiagnostic_OffersGuardFix_ForExpressionBodiedMethod()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                protected override async global::System.Threading.Tasks.Task OnInitializedAsync() => await JS.InvokeVoidAsync("doSomething");
            }
            """;

        var spanStart = source.IndexOf("OnInitializedAsync", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0006", "Components/Counter.razor.cs", spanStart, "OnInitializedAsync".Length);

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Guard with RendererInfo.IsInteractive");

        Assert.Contains("if (RendererInfo.IsInteractive)", updatedSource, StringComparison.Ordinal);
        Assert.Contains("await JS.InvokeVoidAsync(\"doSomething\");", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InteractivityGuardDiagnostic_DoesNotOfferFix_WhenRendererInfoAlreadyReferenced()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                private RenderInfo RendererInfo { get; } = new RenderInfo();
                private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                {
                    if (RendererInfo.IsInteractive)
                    {
                        await JS.InvokeVoidAsync("doSomething");
                    }
                }

                private sealed class RenderInfo
                {
                    public bool IsInteractive { get; } = true;
                }
            }
            """;

        var spanStart = source.IndexOf("OnInitializedAsync", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0006", "Components/Counter.razor.cs", spanStart, "OnInitializedAsync".Length);

        var actions = await CodeFixTestHarness.GetCodeActionsAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider());

        Assert.Empty(actions);
    }

    [Fact]
    public async Task AsyncVoidDiagnostic_OffersTaskReturnTypeFix()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                private async void HandleClick()
                {
                    await global::System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;

        var spanStart = source.IndexOf("async void HandleClick", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0007", "Components/Counter.razor.cs", spanStart, "async void HandleClick".Length);

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Change return type to Task");

        Assert.Contains("private async global::System.Threading.Tasks.Task HandleClick()", updatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("async void HandleClick", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AsyncVoidDiagnostic_DoesNotOfferFix_ForNonAsyncVoidMethod()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                private async global::System.Threading.Tasks.Task HandleClick()
                {
                    await global::System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;

        var spanStart = source.IndexOf("HandleClick", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0007", "Components/Counter.razor.cs", spanStart, "HandleClick".Length);

        var actions = await CodeFixTestHarness.GetCodeActionsAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider());

        Assert.Empty(actions);
    }

    [Fact]
    public async Task CatchWithoutLoggingDiagnostic_OffersRethrowFix()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                private void HandleClick()
                {
                    try
                    {
                        DoWork();
                    }
                    catch (global::System.Exception)
                    {
                    }
                }

                private void DoWork()
                {
                }
            }
            """;

        var spanStart = source.IndexOf("catch", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0008", "Components/Counter.razor.cs", spanStart, "catch".Length);

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Rethrow exception");

        Assert.Contains("catch (global::System.Exception)", updatedSource, StringComparison.Ordinal);
        Assert.Contains("throw;", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatchWithoutLoggingDiagnostic_DoesNotOfferFix_WhenCatchAlreadyRethrows()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                private void HandleClick()
                {
                    try
                    {
                        DoWork();
                    }
                    catch (global::System.Exception)
                    {
                        throw;
                    }
                }

                private void DoWork()
                {
                }
            }
            """;

        var spanStart = source.IndexOf("catch", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0008", "Components/Counter.razor.cs", spanStart, "catch".Length);

        var actions = await CodeFixTestHarness.GetCodeActionsAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider());

        Assert.Empty(actions);
    }

    [Fact]
    public async Task MissingErrorContentDiagnostic_OffersRazorMarkupFix()
    {
        const string source = """
            @rendermode InteractiveServer
            <ErrorBoundary>
                <button @onclick="HandleClick">Click</button>
            </ErrorBoundary>
            """;

        var spanStart = source.IndexOf("ErrorBoundary", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0009", "Components/Counter.razor", spanStart, "ErrorBoundary".Length);

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Add ErrorContent");

        Assert.Contains("<ErrorContent Context=\"exception\">", updatedSource, StringComparison.Ordinal);
        Assert.Contains("<p>@exception.Message</p>", updatedSource, StringComparison.Ordinal);
        Assert.Contains("</ErrorContent>", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingErrorContentDiagnostic_OffersRazorMarkupFix_ForNestedBoundaryWithQuotedAttribute_AndCrLf()
    {
        const string source = "@rendermode InteractiveServer\r\n<ErrorBoundary Title=\"1 > 0\">\r\n    <ErrorBoundary>\r\n        <button @onclick=\"HandleClick\">Click</button>\r\n    </ErrorBoundary>\r\n</ErrorBoundary>\r\n";

        var spanStart = source.IndexOf("<ErrorBoundary Title", StringComparison.Ordinal) + 1;
        var diagnostic = CreateDiagnostic("NTBA0009", "Components/Counter.razor", spanStart, "ErrorBoundary".Length);

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Add ErrorContent");

        Assert.Contains("\r\n", updatedSource, StringComparison.Ordinal);
        Assert.Contains("<ErrorContent Context=\"exception\">", updatedSource, StringComparison.Ordinal);
        Assert.Contains("<p>@exception.Message</p>", updatedSource, StringComparison.Ordinal);
        Assert.Contains("</ErrorContent>", updatedSource, StringComparison.Ordinal);
        Assert.Contains("<ErrorBoundary>\r\n        <button @onclick=\"HandleClick\">Click</button>\r\n    </ErrorBoundary>", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingErrorContentDiagnostic_DoesNotOfferFix_ForSelfClosingBoundary()
    {
        const string source = """
            @rendermode InteractiveServer
            <ErrorBoundary />
            """;

        var spanStart = source.IndexOf("ErrorBoundary", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0009", "Components/Counter.razor", spanStart, "ErrorBoundary".Length);

        var actions = await CodeFixTestHarness.GetCodeActionsAsync(
            path: "Components/Counter.razor",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider());

        Assert.Empty(actions);
    }

    [Fact]
    public async Task MissingErrorContentDiagnostic_DoesNotOfferFix_ForUnmatchedBoundary()
    {
        const string source = """
            @rendermode InteractiveServer
            <ErrorBoundary>
                <button @onclick="HandleClick">Click</button>
            """;

        var spanStart = source.IndexOf("ErrorBoundary", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0009", "Components/Counter.razor", spanStart, "ErrorBoundary".Length);

        var actions = await CodeFixTestHarness.GetCodeActionsAsync(
            path: "Components/Counter.razor",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider());

        Assert.Empty(actions);
    }

    [Fact]
    public async Task LayoutBoundaryDiagnostic_DoesNotOfferFix()
    {
        const string source = """
            @inherits LayoutComponentBase
            <ErrorBoundary>
                @Body
                <ErrorContent>
                    <p>Error</p>
                </ErrorContent>
            </ErrorBoundary>
            """;

        var spanStart = source.IndexOf("ErrorBoundary", StringComparison.Ordinal);
        var diagnostic = CreateDiagnostic("NTBA0010", "Components/MainLayout.razor", spanStart, "ErrorBoundary".Length);

        var actions = await CodeFixTestHarness.GetCodeActionsAsync(
            path: "Components/MainLayout.razor",
            text: source,
            diagnostic,
            new BlazorErrorHandlingCodeFixProvider());

        Assert.Empty(actions);
    }

    private static Diagnostic CreateDiagnostic(string id, string path, int start, int length)
    {
        var descriptor = new DiagnosticDescriptor(
            id,
            id,
            id,
            "Reliability",
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true);
        var text = SourceText.From(new string(' ', start + length + 1));
        var span = new TextSpan(start, length);
        return Diagnostic.Create(descriptor, Location.Create(path, span, text.Lines.GetLinePositionSpan(span)));
    }

}
