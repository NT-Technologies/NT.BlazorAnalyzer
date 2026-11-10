using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class BackgroundFailureAnalyzerTests
{
    private const string Members = """
        private DataService Service { get; } = new();

        private async global::System.Threading.Tasks.Task UnsafeAsync() => await Service.LoadAsync();

        private async global::System.Threading.Tasks.Task SafeAsync()
        {
            try
            {
                await Service.LoadAsync();
            }
            catch (global::System.Exception exception)
            {
                await DispatchExceptionAsync(exception);
            }
        }

        public sealed class DataService
        {
            public global::System.Threading.Tasks.Task LoadAsync() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data");

            public void Load() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data").Wait();

            public event global::System.EventHandler? Changed;
        }
        """;

    private const string InjectNavigation = "[global::Microsoft.AspNetCore.Components.Inject] private global::Microsoft.AspNetCore.Components.NavigationManager Nav { get; set; } = default!; ";

    [Theory]
    [InlineData("private void Start() { _ = Service.LoadAsync(); }", true)]
    [InlineData("private void Start() { Service.LoadAsync(); }", true)]
    [InlineData("private void Start() { _ = UnsafeAsync(); }", true)]
    [InlineData("private void Start() { _ = global::System.Threading.Tasks.Task.Run(async () => await Service.LoadAsync()); }", true)]
    [InlineData("private void Start() { _ = Service.LoadAsync().ConfigureAwait(false); }", true)]
    [InlineData("private async global::System.Threading.Tasks.Task Start() { Service.LoadAsync(); await global::System.Threading.Tasks.Task.CompletedTask; }", false)]
    [InlineData("private void Start() { _ = SafeAsync(); }", false)]
    [InlineData("private void Start() { _ = InvokeAsync(StateHasChanged); }", false)]
    [InlineData("private void Start() { _ = global::System.Threading.Tasks.Task.Delay(10); }", false)]
    [InlineData("private async global::System.Threading.Tasks.Task Start() => await Service.LoadAsync();", false)]
    public async Task UnobservedTask_IsReportedOnlyWhenItsFailureIsLost(string method, bool expectsDiagnostic)
    {
        var diagnostics = await GetDiagnosticsAsync(method);

        Assert.Equal(expectsDiagnostic, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0011"));
    }

    [Theory]
    [InlineData("private readonly global::System.Timers.Timer timer = new(); private void Start() => timer.Elapsed += OnElapsed; private async void OnElapsed(object? sender, global::System.Timers.ElapsedEventArgs args) => await Service.LoadAsync();", true)]
    [InlineData("private readonly global::System.Timers.Timer timer = new(); private void Start() => timer.Elapsed += async (_, _) => await Service.LoadAsync();", true)]
    [InlineData("private void Start() => Service.Changed += (_, _) => Service.Load();", true)]
    [InlineData("private global::System.Threading.Timer? timer; private void Start() => timer = new global::System.Threading.Timer(_ => Service.Load(), null, 0, 1000);", true)]
    [InlineData("private readonly global::System.Timers.Timer timer = new(); private void Start() => timer.Elapsed += OnElapsed; private async void OnElapsed(object? sender, global::System.Timers.ElapsedEventArgs args) { try { await Service.LoadAsync(); } catch (global::System.Exception exception) { await DispatchExceptionAsync(exception); } }", false)]
    [InlineData("private readonly global::System.Timers.Timer timer = new(); private void Start() => timer.Elapsed += (_, _) => InvokeAsync(StateHasChanged);", false)]
    public async Task ExternalCallback_IsReportedOnlyWhenFailuresAreUnhandled(string members, bool expectsDiagnostic)
    {
        var diagnostics = await GetDiagnosticsAsync(members);

        Assert.Equal(expectsDiagnostic, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0012"));
    }

    [Theory]
    // NavigationManager raises outside the renderer; the controls show the same body is reported on an application event.
    [InlineData(InjectNavigation + "private void Start() => Nav.LocationChanged += async (_, _) => await Service.LoadAsync();", true)]
    [InlineData("private void Start() => Service.Changed += (_, _) => Service.Load();", true)]
    // EditContext.OnFieldChanged is raised inside component event handlers, so its failures reach the renderer.
    [InlineData("private readonly global::Microsoft.AspNetCore.Components.Forms.EditContext context = new(new object()); private void Start() => context.OnFieldChanged += (_, _) => Service.Load();", false)]
    // Unsubscribing does not register a callback; the += control with the same handler is reported.
    [InlineData("private void Start() => Service.Changed -= OnChanged; private void OnChanged(object? sender, global::System.EventArgs args) => Service.Load();", false)]
    [InlineData("private void Start() => Service.Changed += OnChanged; private void OnChanged(object? sender, global::System.EventArgs args) => Service.Load();", true)]
    // Events declared in the System namespace are framework events and excluded.
    [InlineData("private void Start() => global::System.AppDomain.CurrentDomain.UnhandledException += (_, _) => Service.Load();", false)]
    public async Task EventSubscription_IsReportedOnlyForExternalEventsBeingSubscribed(string members, bool expectsDiagnostic)
    {
        var diagnostics = await GetDiagnosticsAsync(members);

        Assert.Equal(expectsDiagnostic, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0012"));
    }

    [Fact]
    public async Task ElapsedHandlerWrappedInExplicitDelegate_IsReported()
    {
        var diagnostics = await GetDiagnosticsAsync(
            "private readonly global::System.Timers.Timer timer = new(); " +
            "private void Start() => timer.Elapsed += new global::System.Timers.ElapsedEventHandler(OnElapsed); " +
            "private async void OnElapsed(object? sender, global::System.Timers.ElapsedEventArgs args) => await Service.LoadAsync();");

        Assert.Single(diagnostics, static diagnostic => diagnostic.Id == "NTBA0012");
    }

    [Theory]
    // A non-async lambda is its own function: CS4014 does not cover it, so it is reported even inside an async method.
    [InlineData("private async global::System.Threading.Tasks.Task Start() { global::System.Action run = () => { Service.LoadAsync(); }; run(); await global::System.Threading.Tasks.Task.CompletedTask; }", true)]
    [InlineData("private void Start() { global::System.Action run = () => { Service.LoadAsync(); }; run(); }", true)]
    // Async lambda and async local function: the compiler already warns (CS4014), so the analyzer stays quiet.
    [InlineData("private void Start() { global::System.Func<global::System.Threading.Tasks.Task> run = async () => { Service.LoadAsync(); await global::System.Threading.Tasks.Task.CompletedTask; }; _ = run; }", false)]
    [InlineData("private void Start() { async global::System.Threading.Tasks.Task Local() { Service.LoadAsync(); await global::System.Threading.Tasks.Task.CompletedTask; } _ = Local; }", false)]
    public async Task BareTaskStatement_IsJudgedByTheNearestEnclosingFunction(string method, bool expectsDiagnostic)
    {
        var diagnostics = await GetDiagnosticsAsync(method);

        Assert.Equal(expectsDiagnostic, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0011"));
    }

    [Fact]
    public async Task TaskRunInsideCatchingTry_IsStillReported_BecauseCatchCannotObserveTheTask()
    {
        var diagnostics = await GetDiagnosticsAsync(
            "private void Report(global::System.Exception exception) { } " +
            "private void Start() { try { _ = global::System.Threading.Tasks.Task.Run(async () => await Service.LoadAsync()); } catch (global::System.Exception exception) { Report(exception); } }");

        Assert.Single(diagnostics, static diagnostic => diagnostic.Id == "NTBA0011");
    }

    [Theory]
    [InlineData("private void Start() { var pending = Service.LoadAsync(); }", false)]
    [InlineData("private void Start() { _ = Service.LoadAsync(); }", true)]
    public async Task NamedTaskAssignment_IsNotAnUnobservedDiscard(string method, bool expectsDiagnostic)
    {
        var diagnostics = await GetDiagnosticsAsync(method);

        Assert.Equal(expectsDiagnostic, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0011"));
    }

    [Fact]
    public async Task NonComponentClass_IsNotReported_WhileComponentWithSameCallIs()
    {
        var plain = new SourceFile("Plain.cs", """
            namespace TestComponents;

            public sealed class Plain
            {
                public void Start() { _ = new Worker.DataService().LoadAsync(); }
            }
            """);

        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(CreateWorker("private void Start() { _ = Service.LoadAsync(); }"), plain);

        var reported = Assert.Single(diagnostics, static diagnostic => diagnostic.Id == "NTBA0011");
        Assert.NotEqual("Plain.cs", reported.Location.SourceTree!.FilePath);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task ComponentWithoutRenderMode_IsInteractiveOnlyInStandaloneWebAssembly(bool standalone, bool expectsDiagnostic)
    {
        var program = standalone
            ? "public static class Program { public static void Main(global::Microsoft.AspNetCore.Components.WebAssembly.Hosting.WebAssemblyHostBuilder builder) => builder.RootComponents.Add(); }"
            : "public static class Program { }";
        var component = TestComponentSources.CreateStaticComponent(
            componentName: "Worker",
            renderTreeStatements: """
                __builder.OpenElement(0, "div");
                __builder.CloseElement();
                """,
            razorMethods: Members + Environment.NewLine + "private void Start() { _ = Service.LoadAsync(); }");

        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            component,
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
                        public void Add() { }
                    }
                }
                """));

        Assert.Equal(expectsDiagnostic, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0011"));
    }

    [Theory]
    // The callee is followed and judged by what it does, whether it is in the same file or another partial file.
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task MemberCallee_IsFollowedInAnyPartialFileOfTheComponent(bool calleeInSameFile, bool expectsDiagnostic)
    {
        const string callee = "private async global::System.Threading.Tasks.Task LoadFromPartialAsync() => await Service.LoadAsync();";
        var component = CreateWorker("private void Start() { _ = LoadFromPartialAsync(); }" + (calleeInSameFile ? Environment.NewLine + callee : string.Empty));
        var sources = calleeInSameFile
            ? [component]
            : new[] { component, TestComponentSources.CreateCodeBehind("Worker", callee) };

        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(sources);

        Assert.Equal(expectsDiagnostic, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0011"));
    }

    private static SourceFile CreateWorker(string members) =>
        TestComponentSources.CreateInteractiveComponent(
            componentName: "Worker",
            renderTreeStatements: """
                __builder.OpenElement(0, "div");
                __builder.CloseElement();
                """,
            razorMethods: Members + Environment.NewLine + members);

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> GetDiagnosticsAsync(string members) =>
        AnalyzerTestHarness.GetDiagnosticsAsync(CreateWorker(members));
}
