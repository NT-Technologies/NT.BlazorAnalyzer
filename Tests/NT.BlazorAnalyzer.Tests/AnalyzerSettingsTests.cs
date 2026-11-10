using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class AnalyzerSettingsTests
{
    private const string LifecycleRenderTree = """
        __builder.OpenElement(0, "div");
        __builder.CloseElement();
        """;

    [Fact]
    public async Task StaticSsrComponent_WithCascadingHttpContext_DoesNotReportLifecycleDiagnostic()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            TestComponentSources.CreateStaticComponent(
                componentName: "Login",
                renderTreeStatements: LifecycleRenderTree,
                razorMethods: """
                    [global::Microsoft.AspNetCore.Components.CascadingParameter]
                    private global::Microsoft.AspNetCore.Http.HttpContext HttpContext { get; set; } = default!;

                    private DataService Service { get; } = new();

                    protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                    {
                        await Service.LoadAsync();
                    }

                    private sealed class DataService
                    {
                        public global::System.Threading.Tasks.Task LoadAsync() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data");
                    }
                    """),
            new SourceFile("Http/HttpContext.cs", "namespace Microsoft.AspNetCore.Http { public abstract class HttpContext { } }"));

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "NTBA0003");
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("ntba.safe_types=TestComponents.LifecycleComponent.DataService, TestComponents.ResultExtensions", false)]
    [InlineData("ntba.failure_handling_methods=OnFailureAsync", false)]
    [InlineData("ntba.assume_owner_error_boundary=true", false)]
    public async Task LifecycleCall_HonorsConfiguredSettings(string? option, bool expectsNtba0003)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "LifecycleComponent",
                    renderTreeStatements: LifecycleRenderTree,
                    razorMethods: """
                        private DataService Service { get; } = new();

                        protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                        {
                            await Service.LoadAsync().OnFailureAsync();
                        }

                        private sealed class DataService
                        {
                            public global::System.Threading.Tasks.Task LoadAsync() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data");
                        }
                        """),
                new SourceFile("ResultExtensions.cs", """
                    namespace TestComponents;

                    public static class ResultExtensions
                    {
                        public static global::System.Threading.Tasks.Task OnFailureAsync(this global::System.Threading.Tasks.Task task) => task;
                    }
                    """)
            ],
            globalOptions: ParseOption(option));

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("ntba.failure_prone_type_suffixes=Gateway", true)]
    public async Task SynchronousCall_UsesConfiguredTypeSuffixes(string? option, bool expectsNtba0003)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "LifecycleComponent",
                    renderTreeStatements: LifecycleRenderTree,
                    razorMethods: """
                        // An interface with no implementation in the compilation is judged by naming alone.
                        private IDataGateway Gateway { get; } = null!;

                        protected override void OnInitialized()
                        {
                            Gateway.GetData();
                        }

                        public interface IDataGateway
                        {
                            int GetData();
                        }
                        """)
            ],
            globalOptions: ParseOption(option));

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
    }

    [Fact]
    public async Task AssumeOwnerErrorBoundary_SuppressesBoundaryAndEntryRules()
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Counter",
                    renderTreeStatements: """
                        __builder.OpenElement(0, "button");
                        __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                        __builder.CloseElement();
                        """,
                    razorMethods: """
                        private void HandleClick() => throw new global::System.InvalidOperationException();
                        """)
            ],
            globalOptions: ParseOption("ntba.assume_owner_error_boundary=true"));

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id is "NTBA0001" or "NTBA0002");
    }

    // Behavior source for the tests below: README.md "Configuration" and docs/rules/README.md "Analyzer settings".

    private const string ProbePath = "Components/Probe.razor.g.cs";

    private const string ProbeTypes = """
        // Interfaces with no implementation in the compilation are judged by naming alone.
        public interface IDataService
        {
            int GetData();
            int SyncAll();
            int Zap();
            global::System.Threading.Tasks.Task LoadAsync();
        }

        public interface IDataGateway
        {
            int GetData();
        }
        """;

    [Theory]
    [InlineData("ntba.failure_prone_method_prefixes=Sync", "SyncAll", true)]
    [InlineData("ntba.failure_prone_method_prefixes=Sync", "GetData", false)]
    [InlineData(null, "SyncAll", false)]
    [InlineData(null, "GetData", true)]
    [InlineData("ntba.failure_prone_method_prefixes= Sync, ,Get", "SyncAll", true)]
    [InlineData("ntba.failure_prone_method_prefixes= Sync, ,Get", "GetData", true)]
    [InlineData("ntba.failure_prone_method_prefixes= Sync, ,Get", "Zap", false)]
    public async Task SynchronousCall_UsesConfiguredMethodPrefixesInsteadOfDefaults(string? option, string method, bool expectsNtba0003)
    {
        var diagnostics = await RunProbeAsync($$"""
            private IDataService Data { get; } = null!;
            protected override void OnInitialized() { Data.{{method}}(); }
            """, global: ParseOption(option));

        Assert.Equal(expectsNtba0003, HasNtba0003(diagnostics));
    }

    [Theory]
    [InlineData("ntba.failure_prone_type_suffixes=Gateway", "IDataService", false)]
    [InlineData("ntba.failure_prone_type_suffixes=Gateway", "IDataGateway", true)]
    [InlineData(null, "IDataService", true)]
    [InlineData(null, "IDataGateway", false)]
    public async Task SynchronousCall_UsesConfiguredTypeSuffixesInsteadOfDefaults(string? option, string receiverType, bool expectsNtba0003)
    {
        var diagnostics = await RunProbeAsync($$"""
            private {{receiverType}} Data { get; } = null!;
            protected override void OnInitialized() { Data.GetData(); }
            """, global: ParseOption(option));

        Assert.Equal(expectsNtba0003, HasNtba0003(diagnostics));
    }

    [Theory]
    [InlineData(true, "ntba.assume_owner_error_boundary= TRUE ", false)]
    [InlineData(true, "ntba.assume_owner_error_boundary=garbage", true)]
    [InlineData(true, "ntba.assume_owner_error_boundary=   ", true)]
    [InlineData(true, "ntba.assume_owner_error_boundary=", true)]
    [InlineData(true, null, true)]
    [InlineData(false, "ntba.assume_owner_error_boundary=   ", false)]
    [InlineData(false, "ntba.assume_owner_error_boundary=", false)]
    [InlineData(false, null, false)]
    [InlineData(false, "ntba.assume_owner_error_boundary=garbage", true)]
    [InlineData(false, "ntba.assume_owner_error_boundary=false", true)]
    public async Task AssumeOwnerErrorBoundary_ParsesValues_AndFallsBackToLibraryDetectionWhenBlank(bool hasRenderMode, string? option, bool expectsNtba0003)
    {
        // A project with a render-mode component is not a library (default false); one with only a
        // render-mode-less component is (default true), so a blank value must follow that detection.
        var diagnostics = await RunProbeAsync("""
            protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
            {
                await new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test");
            }
            """, global: ParseOption(option), interactive: hasRenderMode);

        Assert.Equal(expectsNtba0003, HasNtba0003(diagnostics));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("ntba.unknown_awaits_are_failure_prone=True", true)]
    [InlineData("ntba.unknown_awaits_are_failure_prone= TRUE ", true)]
    [InlineData("ntba.unknown_awaits_are_failure_prone=garbage", false)]
    [InlineData("ntba.unknown_awaits_are_failure_prone=", false)]
    public async Task UnknownAwaitedCall_IsFailureProneOnlyWhenSettingParsesAsTrue(string? option, bool expectsNtba0003)
    {
        var diagnostics = await RunProbeAsync("""
            private IHelper Helper { get; } = null!;
            protected override async global::System.Threading.Tasks.Task OnInitializedAsync() { await Helper.RunAsync(); }
            public interface IHelper { global::System.Threading.Tasks.Task RunAsync(); }
            """, global: ParseOption(option));

        Assert.Equal(expectsNtba0003, HasNtba0003(diagnostics));
    }

    [Theory]
    [InlineData("TestComponents.Probe.IAlphaService, ,TestComponents.Probe.IBetaService", false)]
    [InlineData("  TestComponents.Probe.IAlphaService ,TestComponents.Probe.IBetaService  ", false)]
    [InlineData("TestComponents.Probe.IAlphaService, ,", true)]
    [InlineData("TestComponents.Probe.IAlphaService", true)]
    public async Task ListSetting_TrimsEntriesAndIgnoresEmptyOnes(string safeTypes, bool expectsNtba0003)
    {
        var diagnostics = await RunProbeAsync("""
            private IAlphaService Alpha { get; } = null!;
            private IBetaService Beta { get; } = null!;
            protected override void OnInitialized() { Alpha.GetData(); Beta.GetData(); }
            public interface IAlphaService { int GetData(); }
            public interface IBetaService { int GetData(); }
            """, global: new Dictionary<string, string> { ["ntba.safe_types"] = safeTypes });

        Assert.Equal(expectsNtba0003, HasNtba0003(diagnostics));
    }

    [Theory]
    [InlineData("tree", null, "Gateway", true)]
    [InlineData("tree", null, null, false)]
    [InlineData("global", "Gateway", "Service", true)]
    [InlineData("global", "Service", "Gateway", false)]
    [InlineData("otherTree", null, "Gateway", true)]
    public async Task Setting_IsReadFromEditorConfigTree_ButGlobalConfigWins(string scope, string? globalSuffixes, string? treeSuffixes, bool expectsNtba0003)
    {
        var tree = treeSuffixes is null
            ? null
            : new Dictionary<string, IReadOnlyDictionary<string, string>>
            {
                [scope == "otherTree" ? "Other.cs" : ProbePath] = new Dictionary<string, string> { ["ntba.failure_prone_type_suffixes"] = treeSuffixes }
            };

        var diagnostics = await RunProbeAsync("""
            private IDataGateway Data { get; } = null!;
            protected override void OnInitialized() { Data.GetData(); }
            """,
            extra: [new SourceFile("Other.cs", "namespace TestComponents; public sealed class Other { }")],
            global: globalSuffixes is null ? null : new Dictionary<string, string> { ["ntba.failure_prone_type_suffixes"] = globalSuffixes },
            tree: tree);

        Assert.Equal(expectsNtba0003, HasNtba0003(diagnostics));
    }

    private const string MatchingTypes = """
        namespace My
        {
            public class Result<T> { }
            public class Base { }
            public class Derived : Base { }
            public interface IMarker { }
            public class Marked : IMarker { }
            public class Safe { }
        }
        """;

    [Theory]
    [InlineData("My.Result<int>", "GetResult", null, true)]
    [InlineData("My.Result<int>", "GetResult", "My.Result", false)]
    [InlineData("My.Result<int>", "GetResult", "My.Other", true)]
    [InlineData("global::System.Threading.Tasks.Task<My.Result<int>>", "GetResultAsync", "My.Result", false)]
    [InlineData("My.Derived", "GetResult", null, true)]
    [InlineData("My.Derived", "GetResult", "My.Base", false)]
    [InlineData("My.Marked", "GetResult", null, true)]
    [InlineData("My.Marked", "GetResult", "My.IMarker", false)]
    [InlineData("global::System.Threading.Tasks.Task<My.Safe>", "GetResultAsync", null, true)]
    [InlineData("global::System.Threading.Tasks.Task<My.Safe>", "GetResultAsync", "My.Safe", false)]
    [InlineData("global::System.Threading.Tasks.ValueTask<My.Safe>", "GetResultAsync", null, true)]
    [InlineData("global::System.Threading.Tasks.ValueTask<My.Safe>", "GetResultAsync", "My.Safe", false)]
    public async Task SafeTypes_MatchGenericBaseInterfaceAndTaskWrappedReturnTypes(string returnType, string method, string? safeTypes, bool expectsNtba0003)
    {
        var awaited = method.EndsWith("Async", StringComparison.Ordinal);
        var diagnostics = await RunProbeAsync($$"""
            private IDataService Data { get; } = null!;
            protected override {{(awaited ? "async global::System.Threading.Tasks.Task OnInitializedAsync()" : "void OnInitialized()")}} { {{(awaited ? "await " : string.Empty)}}Data.{{method}}(); }
            public interface IDataService { {{returnType}} {{method}}(); }
            """,
            extra: [new SourceFile("My/Types.cs", MatchingTypes)],
            global: safeTypes is null ? null : new Dictionary<string, string> { ["ntba.safe_types"] = safeTypes });

        Assert.Equal(expectsNtba0003, HasNtba0003(diagnostics));
    }

    [Theory]
    [InlineData("global::Microsoft.AspNetCore.Components.Authorization", "Auth.GetAuthenticationStateAsync()", false)]
    [InlineData("Other.Authorization", "Auth.GetAuthenticationStateAsync()", true)]
    [InlineData("global::Microsoft.AspNetCore.Components.Authorization", "Data.GetStateAsync()", false)]
    [InlineData("Other.Authorization", "Data.GetStateAsync()", true)]
    public async Task BuiltInSafeTypes_AreMatchedByFullyQualifiedName(string authNamespace, string call, bool expectsNtba0003)
    {
        // The real types come from the framework reference; a same-named type in another namespace is the positive control.
        Assert.Contains("Microsoft.AspNetCore.Components.Authorization.dll", (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!, StringComparison.OrdinalIgnoreCase);

        var diagnostics = await RunProbeAsync($$"""
            private {{authNamespace}}.AuthenticationStateProvider Auth { get; } = null!;
            private IDataService Data { get; } = null!;
            protected override async global::System.Threading.Tasks.Task OnInitializedAsync() { await {{call}}; }
            public interface IDataService { global::System.Threading.Tasks.Task<{{authNamespace}}.AuthenticationState> GetStateAsync(); }
            """,
            extra: [new SourceFile("Other/Auth.cs", """
                namespace Other.Authorization
                {
                    public abstract class AuthenticationStateProvider
                    {
                        public abstract global::System.Threading.Tasks.Task<AuthenticationState> GetAuthenticationStateAsync();
                    }

                    public class AuthenticationState { }
                }
                """)]);

        Assert.Equal(expectsNtba0003, HasNtba0003(diagnostics));
    }

    [Theory]
    [InlineData("bool IsSuccess", false)]
    [InlineData("bool IsSuccessful", false)]
    [InlineData("bool Succeeded", false)]
    [InlineData("bool IsFailure", false)]
    [InlineData("bool IsFailed", false)]
    [InlineData("bool HasFailed", false)]
    [InlineData("bool IsError", false)]
    [InlineData("int IsSuccess", true)]
    [InlineData("bool IsOk", true)]
    public async Task AwaitedCallReturningResultType_IsSafeOnlyForDocumentedBoolProperties(string property, bool expectsNtba0003)
    {
        var diagnostics = await RunProbeAsync("""
            private IDataService Data { get; } = null!;
            protected override async global::System.Threading.Tasks.Task OnInitializedAsync() { await Data.GetResultAsync(); }
            public interface IDataService { global::System.Threading.Tasks.Task<My.Result> GetResultAsync(); }
            """,
            extra: [new SourceFile("My/Result.cs", $"namespace My {{ public sealed class Result {{ public {property} {{ get; init; }} }} }}")]);

        Assert.Equal(expectsNtba0003, HasNtba0003(diagnostics));
    }

    [Theory]
    [InlineData("NTBA0002", null, true)]
    [InlineData("NTBA0002", "TestComponents.Probe.IDataService", false)]
    [InlineData("NTBA0004", null, true)]
    [InlineData("NTBA0004", "TestComponents.Probe.IDataService", false)]
    [InlineData("NTBA0011", null, true)]
    [InlineData("NTBA0011", "TestComponents.Probe.IDataService", false)]
    [InlineData("NTBA0012", null, true)]
    [InlineData("NTBA0012", "TestComponents.Probe.IDataService", false)]
    public async Task SafeTypes_SuppressesRulesBeyondLifecycle(string ruleId, string? safeTypes, bool expectsRule)
    {
        var (renderTree, members) = ruleId switch
        {
            "NTBA0002" => (ClickRenderTree(), "private void HandleClick() { Data.GetData(); }"),
            "NTBA0004" => (LifecycleRenderTree, "public void Dispose() { Data.GetData(); }"),
            "NTBA0011" => (LifecycleRenderTree, "private void Start() { _ = Data.LoadAsync(); }"),
            "NTBA0012" => (LifecycleRenderTree, "private readonly global::System.Timers.Timer timer = new(); private void Start() => timer.Elapsed += (_, _) => Data.GetData();"),
            _ => throw new ArgumentOutOfRangeException(nameof(ruleId))
        };

        var diagnostics = await RunProbeAsync($$"""
            private IDataService Data { get; } = null!;
            {{members}}
            {{ProbeTypes}}
            """,
            renderTree: renderTree,
            global: safeTypes is null ? null : new Dictionary<string, string> { ["ntba.safe_types"] = safeTypes });

        Assert.Equal(expectsRule, diagnostics.Any(diagnostic => diagnostic.Id == ruleId));
    }

    private static string ClickRenderTree() => """
        __builder.OpenElement(0, "button");
        __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
        __builder.CloseElement();
        """;

    private static bool HasNtba0003(IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003");

    private static Task<IReadOnlyList<Microsoft.CodeAnalysis.Diagnostic>> RunProbeAsync(
        string members,
        string? renderTree = null,
        IEnumerable<SourceFile>? extra = null,
        IReadOnlyDictionary<string, string>? global = null,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>? tree = null,
        bool interactive = true)
    {
        // ProbeTypes supplies the shared interfaces unless the caller declared its own members.
        var body = members.Contains("interface IDataService", StringComparison.Ordinal) || members.Contains(ProbeTypes, StringComparison.Ordinal)
            ? members
            : members + Environment.NewLine + ProbeTypes;
        var create = interactive ? TestComponentSources.CreateInteractiveComponent : (Func<string, string, string?, string, string, SourceFile>)TestComponentSources.CreateStaticComponent;
        return AnalyzerTestHarness.GetDiagnosticsAsync(
            [create("Probe", renderTree ?? LifecycleRenderTree, body, "global::Microsoft.AspNetCore.Components.ComponentBase", "TestComponents"), .. extra ?? []],
            globalOptions: global,
            treeOptions: tree);
    }

    private static Dictionary<string, string>? ParseOption(string? option) =>
        option?.Split('=', 2) is [var key, var value] ? new Dictionary<string, string> { [key] = value } : null;
}
