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
    public async Task LifecycleDiagnostic_WrapsBodyAndInjectsLogger_WhenComponentHasNoLogger()
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

        var updatedSource = await ApplyTryCatchFixAsync(source, "NTBA0003", "OnInitialized");

        Assert.Contains("catch (global::System.Exception exception)", updatedSource, StringComparison.Ordinal);
        Assert.Contains("Logger.LogError(exception, \"OnInitialized failed.\");", updatedSource, StringComparison.Ordinal);
        Assert.Contains("Microsoft.AspNetCore.Components.Inject]", updatedSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<Counter> Logger { get; set; } = default!;", updatedSource, StringComparison.Ordinal);
        Assert.Contains("using Microsoft.Extensions.Logging;", updatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("throw;", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecycleDiagnostic_UsesExistingLogger_AndUnusedExceptionName()
    {
        const string source = """
            using Microsoft.Extensions.Logging;

            namespace TestComponents;

            public partial class Counter
            {
                private readonly ILogger<Counter> _logger = default!;

                public void Dispose()
                {
                    var exception = 1;
                    Cleanup(exception);
                }

                private void Cleanup(int value)
                {
                }
            }
            """;

        var updatedSource = await ApplyTryCatchFixAsync(source, "NTBA0004", "Dispose");

        Assert.Contains("catch (global::System.Exception caughtException)", updatedSource, StringComparison.Ordinal);
        Assert.Contains("_logger.LogError(caughtException, \"Dispose failed.\");", updatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Inject", updatedSource, StringComparison.Ordinal);
        Assert.Equal(2, updatedSource.Split("using Microsoft.Extensions.Logging;").Length);
    }

    [Fact]
    public async Task JsInteropDiagnostic_WrapsExpressionBodiedAsyncMethod()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                private async global::System.Threading.Tasks.Task HandleClick() => await JS.InvokeVoidAsync("doSomething");
            }
            """;

        var updatedSource = await ApplyTryCatchFixAsync(source, "NTBA0005", "HandleClick");

        Assert.Contains("private async global::System.Threading.Tasks.Task HandleClick()", updatedSource, StringComparison.Ordinal);
        Assert.Contains("await JS.InvokeVoidAsync(\"doSomething\");", updatedSource, StringComparison.Ordinal);
        Assert.Contains("Logger.LogError(exception, \"HandleClick failed.\");", updatedSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("protected override global::System.Threading.Tasks.Task OnInitializedAsync() => global::System.Threading.Tasks.Task.CompletedTask;", "OnInitializedAsync")]
    [InlineData("private int OnInitialized() => 1;", "OnInitialized")]
    [InlineData("private int Logger => 0; protected override void OnInitialized() { }", "OnInitialized")]
    public async Task TryCatchFix_IsNotOffered_WhenCatchCannotContainFailureOrLoggerNameIsTaken(string member, string methodName)
    {
        var source = $$"""
            namespace TestComponents;

            public partial class Counter
            {
                {{member}}
            }
            """;

        var spanStart = source.IndexOf(methodName, StringComparison.Ordinal);
        var actions = await CodeFixTestHarness.GetCodeActionsAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            CreateDiagnostic("NTBA0003", "Components/Counter.razor.cs", spanStart, methodName.Length),
            new BlazorErrorHandlingCodeFixProvider());

        Assert.Empty(actions);
    }

    [Fact]
    public async Task TryCatchFix_PicksAnExceptionNameThatIsNotAlreadyUsed()
    {
        const string source = """
            using Microsoft.Extensions.Logging;

            namespace TestComponents;

            public partial class Counter
            {
                private readonly ILogger<Counter> _logger = default!;

                public void Dispose()
                {
                    var exception = 1;
                    var caughtException = 2;
                    Cleanup(exception + caughtException);
                }

                private void Cleanup(int value)
                {
                }
            }
            """;

        var updatedSource = await ApplyTryCatchFixAsync(source, "NTBA0004", "Dispose");

        Assert.Contains("catch (global::System.Exception caughtException2)", updatedSource, StringComparison.Ordinal);
        Assert.Contains("_logger.LogError(caughtException2, \"Dispose failed.\");", updatedSource, StringComparison.Ordinal);
    }

    private static Task<string> ApplyTryCatchFixAsync(string source, string diagnosticId, string methodName)
    {
        var spanStart = source.IndexOf(methodName, StringComparison.Ordinal);
        return CodeFixTestHarness.ApplyCodeActionAsync(
            path: "Components/Counter.razor.cs",
            text: source,
            CreateDiagnostic(diagnosticId, "Components/Counter.razor.cs", spanStart, methodName.Length),
            new BlazorErrorHandlingCodeFixProvider(),
            title: "Wrap body in try/catch and log");
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
    public async Task InteractivityGuardDiagnostic_DoesNotOfferFix_ForNonAsyncTaskMethod()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter
            {
                protected override global::System.Threading.Tasks.Task OnInitializedAsync() => global::System.Threading.Tasks.Task.CompletedTask;
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
    public async Task CatchWithoutLoggingDiagnostic_OffersLogExceptionFix()
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
            title: "Log exception");

        Assert.Contains("catch (global::System.Exception exception)", updatedSource, StringComparison.Ordinal);
        Assert.Contains("Logger.LogError(exception, \"HandleClick failed.\");", updatedSource, StringComparison.Ordinal);
        Assert.Contains("using Microsoft.Extensions.Logging;", updatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("throw;", updatedSource, StringComparison.Ordinal);
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

        Assert.Contains("<ErrorContent>", updatedSource, StringComparison.Ordinal);
        Assert.Contains("<p>Something went wrong. Please try again.</p>", updatedSource, StringComparison.Ordinal);
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
        Assert.Contains("<ErrorContent>", updatedSource, StringComparison.Ordinal);
        Assert.Contains("<p>Something went wrong. Please try again.</p>", updatedSource, StringComparison.Ordinal);
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

    private const string Path = "Components/Counter.razor.cs";

    [Fact]
    public async Task TryCatchFix_ReusesProtectedBaseLogger_AndDoesNotInjectOne()
    {
        const string source = """
            using Microsoft.Extensions.Logging;

            namespace TestComponents;

            public class BaseComponent : global::Microsoft.AspNetCore.Components.ComponentBase
            {
                protected ILogger<BaseComponent> BaseLogger { get; } = default!;
            }

            public partial class Counter : BaseComponent
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

        var updatedSource = await ApplyTryCatchFixAsync(source, "NTBA0003", "OnInitialized");

        Assert.Contains("BaseLogger.LogError(exception, \"OnInitialized failed.\");", updatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Inject]", updatedSource, StringComparison.Ordinal);
        Assert.Empty(await CodeFixTestHarness.GetCompilationErrorsAfterFixAsync(Path, source, DiagnosticAt(source, "NTBA0003", "OnInitialized"), new BlazorErrorHandlingCodeFixProvider(), TryCatchTitle));
    }

    [Fact]
    public async Task TryCatchFix_InjectsLogger_WhenBaseLoggerIsPrivate()
    {
        const string source = """
            namespace TestComponents;

            public class BaseComponent : global::Microsoft.AspNetCore.Components.ComponentBase
            {
                private global::Microsoft.Extensions.Logging.ILogger<BaseComponent> HiddenLogger { get; } = default!;
            }

            public partial class Counter : BaseComponent
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

        var updatedSource = await ApplyTryCatchFixAsync(source, "NTBA0003", "OnInitialized");

        Assert.Contains("Logger.LogError(exception, \"OnInitialized failed.\");", updatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("HiddenLogger.LogError", updatedSource, StringComparison.Ordinal);
        Assert.Contains("ILogger<Counter> Logger { get; set; } = default!;", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryCatchFix_InjectsLogger_WhenLoggingNamespaceIsImported()
    {
        const string source = """
            using Microsoft.Extensions.Logging;

            namespace TestComponents;

            public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase
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

        var updatedSource = await ApplyTryCatchFixAsync(source, "NTBA0003", "OnInitialized");

        Assert.Contains("ILogger<Counter> Logger { get; set; } = default!;", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryCatchFix_ReusesMemberTypedExactlyILogger()
    {
        const string source = """
            using Microsoft.Extensions.Logging;

            namespace TestComponents;

            public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase
            {
                private readonly ILogger _log = default!;

                protected override void OnInitialized()
                {
                    DoWork();
                }

                private void DoWork()
                {
                }
            }
            """;

        var updatedSource = await ApplyTryCatchFixAsync(source, "NTBA0003", "OnInitialized");

        Assert.Contains("_log.LogError(exception, \"OnInitialized failed.\");", updatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("Inject]", updatedSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("private async void HandleClick() { await global::System.Threading.Tasks.Task.CompletedTask; }", "HandleClick")]
    [InlineData("private async global::System.Threading.Tasks.ValueTask LoadAsync() { await global::System.Threading.Tasks.Task.CompletedTask; }", "LoadAsync")]
    public async Task TryCatchFix_WrapsAwaitInsideTry_ForAsyncVoidAndAsyncValueTask(string member, string methodName)
    {
        var source = $$"""
            namespace TestComponents;

            public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase
            {
                {{member}}
            }
            """;

        var updatedSource = await ApplyTryCatchFixAsync(source, "NTBA0003", methodName);

        var tryIndex = updatedSource.IndexOf("try", StringComparison.Ordinal);
        var awaitIndex = updatedSource.IndexOf("await global::System.Threading.Tasks.Task.CompletedTask;", StringComparison.Ordinal);
        var catchIndex = updatedSource.IndexOf("catch (global::System.Exception exception)", StringComparison.Ordinal);
        Assert.InRange(awaitIndex, tryIndex + 1, catchIndex - 1);
        Assert.Contains($"Logger.LogError(exception, \"{methodName} failed.\");", updatedSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TryCatchFix_ProducesCompilableCode_ForAsyncVoidAndInjectedLogger()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase
            {
                private async void HandleClick()
                {
                    await global::System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;

        Assert.Empty(await CodeFixTestHarness.GetCompilationErrorsAsync(Path, source));
        var errors = await CodeFixTestHarness.GetCompilationErrorsAfterFixAsync(Path, source, DiagnosticAt(source, "NTBA0003", "HandleClick"), new BlazorErrorHandlingCodeFixProvider(), TryCatchTitle);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task TryCatchFix_ProducesCompilableCode_ForExpressionBodyWithExistingLogger()
    {
        const string source = """
            using Microsoft.Extensions.Logging;

            namespace TestComponents;

            public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase
            {
                private readonly ILogger<Counter> _logger = default!;

                private void Cleanup() => Run();

                private void Run()
                {
                }
            }
            """;

        Assert.Empty(await CodeFixTestHarness.GetCompilationErrorsAsync(Path, source));
        var errors = await CodeFixTestHarness.GetCompilationErrorsAfterFixAsync(Path, source, DiagnosticAt(source, "NTBA0004", "Cleanup"), new BlazorErrorHandlingCodeFixProvider(), TryCatchTitle);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task TryCatchFix_UsesClosedGenericLoggerType_ForGenericComponent()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter<T> : global::Microsoft.AspNetCore.Components.ComponentBase
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

        var updatedSource = await ApplyTryCatchFixAsync(source, "NTBA0003", "OnInitialized");

        Assert.Contains("ILogger<Counter<T>> Logger { get; set; } = default!;", updatedSource, StringComparison.Ordinal);
        Assert.Empty(await CodeFixTestHarness.GetCompilationErrorsAfterFixAsync(Path, source, DiagnosticAt(source, "NTBA0003", "OnInitialized"), new BlazorErrorHandlingCodeFixProvider(), TryCatchTitle));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task LoggingFixes_AreOfferedOnlyWhenLoggingIsReferenced(bool includeLogging, int expectedActions)
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase
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

        var actions = await CodeFixTestHarness.GetCodeActionsAsync(
            Path,
            source,
            DiagnosticAt(source, "NTBA0003", "OnInitialized"),
            new BlazorErrorHandlingCodeFixProvider(),
            includeLogging);

        Assert.Equal(expectedActions, actions.Count);
    }

    [Fact]
    public async Task LogExceptionFix_NoLoggingReference_OffersNothingForCatch()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase
            {
                private void HandleClick()
                {
                    try
                    {
                        DoWork();
                    }
                    catch
                    {
                    }
                }

                private void DoWork()
                {
                }
            }
            """;

        var actions = await CodeFixTestHarness.GetCodeActionsAsync(Path, source, DiagnosticAt(source, "NTBA0008", "catch"), new BlazorErrorHandlingCodeFixProvider(), includeLogging: false);

        Assert.Empty(actions);
    }

    [Fact]
    public async Task LogExceptionFix_BareCatch_GainsExceptionVariableAndCompiles()
    {
        const string source = """
            namespace TestComponents;

            public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase
            {
                private void HandleClick()
                {
                    try
                    {
                        DoWork();
                    }
                    catch
                    {
                    }
                }

                private void DoWork()
                {
                }
            }
            """;
        var diagnostic = DiagnosticAt(source, "NTBA0008", "catch");

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(Path, source, diagnostic, new BlazorErrorHandlingCodeFixProvider(), LogExceptionTitle);

        Assert.Matches(@"catch\s*\(global::System\.Exception exception\)", updatedSource);
        Assert.Contains("Logger.LogError(exception, \"HandleClick failed.\");", updatedSource, StringComparison.Ordinal);
        Assert.Empty(await CodeFixTestHarness.GetCompilationErrorsAsync(Path, source));
        Assert.Empty(await CodeFixTestHarness.GetCompilationErrorsAfterFixAsync(Path, source, diagnostic, new BlazorErrorHandlingCodeFixProvider(), LogExceptionTitle));
    }

    [Fact]
    public async Task LogExceptionFix_NamedCatch_ReusesExistingVariableAndType()
    {
        const string source = """
            using System;

            namespace TestComponents;

            public partial class Counter : global::Microsoft.AspNetCore.Components.ComponentBase
            {
                private void HandleClick()
                {
                    try
                    {
                        DoWork();
                    }
                    catch (Exception ex)
                    {
                    }
                }

                private void DoWork()
                {
                }
            }
            """;
        var diagnostic = DiagnosticAt(source, "NTBA0008", "catch");

        var updatedSource = await CodeFixTestHarness.ApplyCodeActionAsync(Path, source, diagnostic, new BlazorErrorHandlingCodeFixProvider(), LogExceptionTitle);

        Assert.Contains("catch (Exception ex)", updatedSource, StringComparison.Ordinal);
        Assert.Contains("Logger.LogError(ex, \"HandleClick failed.\");", updatedSource, StringComparison.Ordinal);
        Assert.DoesNotContain("global::System.Exception exception", updatedSource, StringComparison.Ordinal);
        Assert.Empty(await CodeFixTestHarness.GetCompilationErrorsAfterFixAsync(Path, source, diagnostic, new BlazorErrorHandlingCodeFixProvider(), LogExceptionTitle));
    }

    private const string TryCatchTitle = "Wrap body in try/catch and log";
    private const string LogExceptionTitle = "Log exception";

    private static Diagnostic DiagnosticAt(string source, string id, string token) =>
        CreateDiagnostic(id, Path, source.IndexOf(token, StringComparison.Ordinal), token.Length);

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
