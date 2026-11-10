using Xunit;

namespace NT.BlazorAnalyzer.Tests;

/// <summary>
/// False-positive classes found on real applications, each pinned next to the true positive it must not hide.
/// </summary>
public sealed class PrecisionRegressionTests
{
    private const string EmptyRenderTree = """
        __builder.OpenElement(0, "div");
        __builder.CloseElement();
        """;

    [Theory]
    [InlineData("public sealed class Result { public bool IsSuccess { get; init; } }", false)]
    [InlineData("public interface IResult { bool HasFailed { get; } } public sealed class Result : IResult { public bool HasFailed => false; }", false)]
    [InlineData("public sealed class Result { public bool IsSuccessStatusCode { get; init; } }", true)]
    public async Task AwaitedCallReturningResultType_IsNotFailureProne(string resultType, bool expectsNtba0003)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Loader",
                renderTreeStatements: EmptyRenderTree,
                razorMethods: $$"""
                    private DataService Service { get; } = new();

                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                    {
                        var result = await Service.LoadAsync();
                    }

                    private sealed class DataService
                    {
                        public global::System.Threading.Tasks.Task<Result> LoadAsync() => global::System.Threading.Tasks.Task.FromResult(global::System.Text.Json.JsonSerializer.Deserialize<Result>("{}")!);
                    }

                    {{resultType}}
                    """));

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
    }

    [Theory]
    [InlineData("await Toasts.ShowErrorAsync();", false)]
    [InlineData("await OnLoaded.InvokeAsync();", false)]
    [InlineData("await Notifications.SendAsync();", true)]
    public async Task UiNotificationsAndCallbacks_AreNotFailureProne(string statement, bool expectsNtba0003)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [TestComponentSources.CreateInteractiveComponent(
                componentName: "Loader",
                renderTreeStatements: EmptyRenderTree,
                razorMethods: $$"""
                    private AppToastService Toasts { get; } = new();
                    private NotificationService Notifications { get; } = new();
                    [global::Microsoft.AspNetCore.Components.Parameter] public global::Microsoft.AspNetCore.Components.EventCallback OnLoaded { get; set; }

                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                    {
                        {{statement}}
                    }

                    private sealed class AppToastService
                    {
                        public global::System.Threading.Tasks.Task ShowErrorAsync() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/toast");
                    }

                    private sealed class NotificationService
                    {
                        public global::System.Threading.Tasks.Task SendAsync() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/notify");
                    }
                    """)],
            globalOptions: new Dictionary<string, string> { ["ntba.unknown_awaits_are_failure_prone"] = "true" });

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
    }

    [Theory]
    [InlineData("confirm", false)]
    [InlineData("history.back", false)]
    [InlineData("toString", false)]
    [InlineData("app.saveDraft", true)]
    public async Task BuiltInBrowserCalls_AreNotFailureProneJsInterop(string identifier, bool expectsNtba0005)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Editor",
                renderTreeStatements: EmptyRenderTree,
                razorMethods: $$"""
                    [global::Microsoft.AspNetCore.Components.Inject] private global::Microsoft.JSInterop.IJSRuntime JS { get; set; } = default!;

                    private async global::System.Threading.Tasks.Task HandleClick()
                    {
                        await JS.InvokeVoidAsync("{{identifier}}");
                    }
                    """));

        Assert.Equal(expectsNtba0005, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0005"));
    }

    [Theory]
    [InlineData("DisposeAsyncCore", "", false)]
    [InlineData("ReleaseModuleAsync", "await module.DisposeAsync();", false)]
    [InlineData("ReleaseModuleAsync", "", true)]
    public async Task JsCleanupSwallowingOnlyDisconnects_FollowsDocumentedDisposalPattern(string helperName, string disposeModule, bool expectsDiagnostic)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Editor",
                renderTreeStatements: EmptyRenderTree,
                razorMethods: $$"""
                    private global::Microsoft.JSInterop.IJSObjectReference? module;

                    public async global::System.Threading.Tasks.ValueTask DisposeAsync()
                    {
                        await {{helperName}}();
                    }

                    private async global::System.Threading.Tasks.ValueTask {{helperName}}()
                    {
                        if (module is null)
                        {
                            return;
                        }

                        try
                        {
                            await module.InvokeVoidAsync("onDispose");
                            {{disposeModule}}
                        }
                        catch (global::Microsoft.JSInterop.JSDisconnectedException)
                        {
                        }
                    }
                    """));

        Assert.Equal(expectsDiagnostic, diagnostics.Any(static diagnostic => diagnostic.Id is "NTBA0004" or "NTBA0005"));
    }

    [Fact]
    public async Task FireAndForgetMemberCall_IsReportedAsUnobservedTaskNotLifecycleFailure()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Banner",
                renderTreeStatements: EmptyRenderTree,
                razorMethods: """
                    private DataService Service { get; } = new();

                    protected override void OnInitialized()
                    {
                        _ = RotateAsync();
                    }

                    private async global::System.Threading.Tasks.Task RotateAsync() => await Service.LoadAsync();

                    private sealed class DataService
                    {
                        public global::System.Threading.Tasks.Task LoadAsync() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data");
                    }
                    """));

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "NTBA0003");
        Assert.Contains(diagnostics, static diagnostic => diagnostic.Id == "NTBA0011");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", true)]
    public async Task ComponentLibrary_AssumesOwnerBoundaryUnlessConfigured(string? assumeOwner, bool expectsNtba0003)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [
                TestComponentSources.CreateStaticComponent(
                    componentName: "LibraryCard",
                    renderTreeStatements: EmptyRenderTree,
                    razorMethods: """
                        private DataService Service { get; } = new();

                        protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                        {
                            await Service.LoadAsync();
                        }

                        private sealed class DataService
                        {
                            public global::System.Threading.Tasks.Task LoadAsync() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data");
                        }
                        """)
            ],
            globalOptions: assumeOwner is null ? null : new Dictionary<string, string> { ["ntba.assume_owner_error_boundary"] = assumeOwner });

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
    }

    [Theory]
    [InlineData("_error = \"Loading failed. Try again.\";", false)]
    [InlineData("Toasts.ShowError();", false)]
    [InlineData("_items = new();", true)]
    [InlineData("", true)]
    public async Task GeneralCatch_IsSilentOnlyWhenNothingReachesTheUser(string catchBody, bool expectsNtba0008)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateInteractiveComponent(
                componentName: "Loader",
                renderTreeStatements: EmptyRenderTree,
                razorMethods: $$"""
                    private string? _error;
                    private global::System.Collections.Generic.List<int> _items = new();
                    private AppToastService Toasts { get; } = new();

                    private void Load()
                    {
                        try
                        {
                            _items.Add(1);
                        }
                        catch (global::System.Exception)
                        {
                            {{catchBody}}
                        }
                    }

                    private sealed class AppToastService
                    {
                        public void ShowError()
                        {
                        }
                    }
                    """));

        Assert.Equal(expectsNtba0008, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0008"));
    }

    [Fact]
    public async Task PeriodicTimerLoop_IsNotFailureProne()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [TestComponentSources.CreateInteractiveComponent(
                componentName: "Banner",
                renderTreeStatements: EmptyRenderTree,
                razorMethods: """
                    private readonly global::System.Threading.PeriodicTimer _timer = new(global::System.TimeSpan.FromSeconds(5));

                    protected override void OnInitialized() => _ = RotateAsync();

                    private async global::System.Threading.Tasks.Task RotateAsync()
                    {
                        while (await _timer.WaitForNextTickAsync())
                        {
                            await InvokeAsync(StateHasChanged);
                        }
                    }
                    """)],
            globalOptions: new Dictionary<string, string> { ["ntba.unknown_awaits_are_failure_prone"] = "true" });

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id is "NTBA0003" or "NTBA0011");
    }

    [Theory]
    [InlineData("public async global::System.Threading.Tasks.Task<bool> IsEnabledAsync() => (await External.FetchAsync()).Length > 0;", false)]
    [InlineData("public global::System.Threading.Tasks.Task<bool> IsEnabledAsync() => throw new global::System.InvalidOperationException(\"Only during static rendering.\");", false)]
    [InlineData("public async global::System.Threading.Tasks.Task<bool> IsEnabledAsync() => (await Http.GetStringAsync(\"flags\")).Length > 0;", true)]
    public async Task CallIntoProjectCode_IsJudgedByWhatTheCalleeDoes(string implementation, bool expectsNtba0003)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "FeatureGate",
                    renderTreeStatements: EmptyRenderTree,
                    razorMethods: """
                        private global::TestComponents.IFeatureService Features { get; } = new global::TestComponents.FeatureService();

                        protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                        {
                            var enabled = await Features.IsEnabledAsync();
                        }
                        """),
                new SourceFile("Services/FeatureService.cs", $$"""
                    namespace TestComponents;

                    public interface IFeatureService
                    {
                        global::System.Threading.Tasks.Task<bool> IsEnabledAsync();
                    }

                    public interface IExternalClient
                    {
                        global::System.Threading.Tasks.Task<string> FetchAsync();
                    }

                    public sealed class FeatureService : IFeatureService
                    {
                        private IExternalClient External { get; } = null!;
                        private global::System.Net.Http.HttpClient Http { get; } = new();

                        {{implementation}}
                    }
                    """)
            ]);

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("true", true)]
    public async Task UnknownAwaitedCall_CountsOnlyInStrictMode(string? strict, bool expectsNtba0003)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Loader",
                    renderTreeStatements: EmptyRenderTree,
                    razorMethods: """
                        private global::TestComponents.IWidgetFeed Feed { get; } = null!;

                        protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                        {
                            await Feed.PollAsync();
                        }
                        """),
                new SourceFile("Services/IWidgetFeed.cs", """
                    namespace TestComponents;

                    public interface IWidgetFeed
                    {
                        global::System.Threading.Tasks.Task PollAsync();
                    }
                    """)
            ],
            globalOptions: strict is null ? null : new Dictionary<string, string> { ["ntba.unknown_awaits_are_failure_prone"] = strict });

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task RoutablePageWithoutRenderMode_IsStaticUnlessRoutesAreGloballyInteractive(bool globallyInteractive, bool expectsNtba0003)
    {
        var appStatements = globallyInteractive
            ? """
                __builder.OpenComponent<global::TestComponents.Routes>(0);
                __builder.AddComponentRenderMode(global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer);
                __builder.CloseComponent();
                """
            : """
                __builder.OpenComponent<global::TestComponents.Routes>(0);
                __builder.CloseComponent();
                """;
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [
                TestComponentSources.CreateStaticComponent(
                    componentName: "Error",
                    renderTreeStatements: EmptyRenderTree,
                    razorMethods: """
                        private global::System.Net.Http.HttpClient Http { get; } = new();

                        protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                        {
                            await Http.GetStringAsync("https://example.test/error");
                        }
                        """),
                new SourceFile("Components/ErrorRoute.cs", """
                    namespace TestComponents;

                    [global::Microsoft.AspNetCore.Components.Route("/Error")]
                    public partial class Error
                    {
                    }
                    """),
                TestComponentSources.CreateStaticComponent(componentName: "Routes", renderTreeStatements: EmptyRenderTree),
                TestComponentSources.CreateStaticComponent(componentName: "App", renderTreeStatements: appStatements)
            ]);

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
    }

    [Theory]
    [InlineData("using var context = Factory.CreateDbContext();", false)]
    [InlineData("await Context.DisposeAsync();", false)]
    [InlineData("await Context.SaveChangesAsync();", true)]
    public async Task EfCoreContextLifecycle_IsNotDatabaseIo(string statement, bool expectsNtba0003)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Movies",
                    renderTreeStatements: EmptyRenderTree,
                    razorMethods: $$"""
                        private global::Microsoft.EntityFrameworkCore.IDbContextFactory Factory { get; } = null!;
                        private global::Microsoft.EntityFrameworkCore.DbContext Context { get; } = null!;

                        protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                        {
                            {{statement}}
                            await global::System.Threading.Tasks.Task.CompletedTask;
                        }
                        """),
                // Members without bodies stand in for the EF Core package, so they are judged as library APIs.
                new SourceFile("EfCore.cs", """
                    namespace Microsoft.EntityFrameworkCore;

                    public interface IDbContextFactory
                    {
                        DbContext CreateDbContext();
                    }

                    public abstract class DbContext : global::System.IAsyncDisposable, global::System.IDisposable
                    {
                        public abstract global::System.Threading.Tasks.Task<int> SaveChangesAsync();
                        public abstract global::System.Threading.Tasks.ValueTask DisposeAsync();
                        public abstract void Dispose();
                    }
                    """)
            ]);

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task StandaloneWebAssemblyComponents_AreInteractiveWithoutRenderMode(bool standalone, bool expectsNtba0005)
    {
        var program = standalone
            ? "public static class Program { public static void Main(global::Microsoft.AspNetCore.Components.WebAssembly.Hosting.WebAssemblyHostBuilder builder) => builder.RootComponents.Add(); }"
            : "public static class Program { }";
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [
                TestComponentSources.CreateStaticComponent(
                    componentName: "Editor",
                    renderTreeStatements: EmptyRenderTree,
                    razorMethods: """
                        [global::Microsoft.AspNetCore.Components.Inject] private global::Microsoft.JSInterop.IJSRuntime JS { get; set; } = default!;

                        private async global::System.Threading.Tasks.Task SaveAsync() => await JS.InvokeVoidAsync("app.saveDraft");
                        """),
                new SourceFile("Program.cs", program),
                new SourceFile("Hosting.cs", """
                    namespace Microsoft.AspNetCore.Components.WebAssembly.Hosting
                    {
                        public sealed class WebAssemblyHostBuilder
                        {
                            public RootComponentList RootComponents { get; } = new();
                        }

                        public sealed class RootComponentList
                        {
                            public void Add()
                            {
                            }
                        }
                    }
                    """)
            ]);

        Assert.Equal(expectsNtba0005, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0005"));
    }
}
