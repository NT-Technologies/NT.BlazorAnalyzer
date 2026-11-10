using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

/// <summary>
/// Branches of the failure-detection and coverage logic: catch semantics, JS-disconnect cleanup, source following,
/// known I/O APIs, owner coverage, static SSR, routes detection and boundary fallback UI. Every row has a control row
/// that flips the verdict, so no assertion passes because the analyzer reports (or ignores) everything.
/// </summary>
public sealed class SemanticsCoverageTests
{
    private const string EmptyRenderTree = """
        __builder.OpenElement(0, "div");
        __builder.CloseElement();
        """;

    private const string HttpCall = "new global::System.Net.Http.HttpClient().GetStringAsync(\"https://example.test\")";

    private static readonly IReadOnlyDictionary<string, string> NoOwner = new Dictionary<string, string> { ["ntba.assume_owner_error_boundary"] = "false" };

    private static HashSet<string> Ids(IEnumerable<Diagnostic> diagnostics) => [.. diagnostics.Select(static diagnostic => diagnostic.Id)];

    private static async Task<HashSet<string>> IdsAsync(IEnumerable<SourceFile> sources, IEnumerable<AdditionalSourceFile>? files = null, IReadOnlyDictionary<string, string>? options = null) =>
        Ids(await AnalyzerTestHarness.GetDiagnosticsAsync(sources, files, options));

    private static Task<HashSet<string>> IdsAsync(string razorMethods, IReadOnlyDictionary<string, string>? options = null, params SourceFile[] extra) =>
        IdsAsync([TestComponentSources.CreateInteractiveComponent("Loader", EmptyRenderTree, razorMethods), .. extra], null, options);

    // ---- 1. Catch semantics ----

    [Theory]
    [InlineData("catch (global::System.OperationCanceledException) { } catch (global::System.Exception ex) { global::System.Console.Error.WriteLine(ex); }", false, false)]
    [InlineData("catch (global::System.OperationCanceledException) { }", true, false)]
    [InlineData("catch (global::System.OperationCanceledException) { } catch (global::System.Exception) { }", false, true)]
    [InlineData("catch { }", false, true)]
    [InlineData("catch { throw; }", true, false)]
    [InlineData("catch (global::System.Exception ex) { global::System.Console.Error.WriteLine(ex); global::System.Action fail = () => { throw new global::System.InvalidOperationException(); }; }", false, false)]
    [InlineData("catch (global::System.Exception ex) { global::System.Console.Error.WriteLine(ex); void Fail() { throw new global::System.InvalidOperationException(); } }", false, false)]
    [InlineData("catch (global::System.Exception ex) { global::System.Console.Error.WriteLine(ex); if (_error is null) { throw; } }", true, false)]
    [InlineData("catch (global::System.Exception ex) { global::System.Console.Error.WriteLine(ex); throw new global::System.InvalidOperationException(); }", true, false)]
    [InlineData("catch (global::System.Net.Http.HttpRequestException) { }", false, false)]
    public async Task LifecycleCatchChains_DecideContainmentAndObservabilityIndependently(string catches, bool expectsNtba0003, bool expectsNtba0008)
    {
        var ids = await IdsAsync($$"""
            private string? _error;

            protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
            {
                try
                {
                    await {{HttpCall}};
                }
                {{catches}}
            }
            """);

        Assert.Equal(expectsNtba0003, ids.Contains("NTBA0003"));
        Assert.Equal(expectsNtba0008, ids.Contains("NTBA0008"));
    }

    [Theory]
    [InlineData("_error = $\"Load failed at {global::System.DateTime.Now}\";", false)]
    [InlineData("_error = ex.Message;", false)]
    [InlineData("_error = null;", true)]
    [InlineData("_error = \"\";", true)]
    [InlineData("_error = string.Empty;", true)]
    [InlineData("var message = ex.Message;", true)]
    [InlineData("global::System.Action show = () => _error = \"Failed\";", true)]
    public async Task GeneralCatch_SurfacesFailureOnlyThroughAMessageStoredInState(string catchBody, bool expectsNtba0008)
    {
        var ids = await IdsAsync($$"""
            private string? _error;
            private global::System.Collections.Generic.List<int> _items = new();

            private void Load()
            {
                try
                {
                    _items.Add(1);
                }
                catch (global::System.Exception ex)
                {
                    {{catchBody}}
                }
            }
            """);

        Assert.Equal(expectsNtba0008, ids.Contains("NTBA0008"));
    }

    // ---- 2. JS disconnect ----

    [Theory]
    [InlineData("catch (global::Microsoft.JSInterop.JSDisconnectedException) { }", true, false)]
    [InlineData("catch (global::Microsoft.JSInterop.JSDisconnectedException) { } catch (global::System.Exception) { }", false, true)]
    [InlineData("catch (global::Microsoft.JSInterop.JSDisconnectedException) { } catch (global::System.Exception ex) { global::System.Console.Error.WriteLine(ex); }", false, false)]
    [InlineData("catch (global::Microsoft.JSInterop.JSDisconnectedException) { } catch (global::System.Exception ex) { global::System.Console.Error.WriteLine(ex); throw; }", true, false)]
    public async Task JsDisconnectCatchOutsideDisposal_DoesNotContainTheFailure(string catches, bool expectsNtba0005, bool expectsNtba0008)
    {
        var ids = await IdsAsync($$"""
            [global::Microsoft.AspNetCore.Components.Inject] private global::Microsoft.JSInterop.IJSRuntime JS { get; set; } = default!;

            private async global::System.Threading.Tasks.Task HandleClick()
            {
                try
                {
                    await JS.InvokeVoidAsync("app.x");
                }
                {{catches}}
            }
            """);

        Assert.Equal(expectsNtba0005, ids.Contains("NTBA0005"));
        Assert.Equal(expectsNtba0008, ids.Contains("NTBA0008"));
    }

    [Theory]
    [InlineData("DisposeDirect", false)]
    [InlineData("DisposeCoreHelper", false)]
    [InlineData("DisposeAsyncCoreHelper", false)]
    [InlineData("OtherHelper", true)]
    [InlineData("NonJsWorkInTry", true)]
    public async Task JsCleanupSwallowingOnlyDisconnects_IsAllowedInDisposalHelpersOnly(string shape, bool expectsDiagnostic)
    {
        const string catchDisconnect = "catch (global::Microsoft.JSInterop.JSDisconnectedException) { }";
        var (disposeBody, helper) = shape switch
        {
            "DisposeDirect" => ($"try {{ await JS.InvokeVoidAsync(\"app.x\"); }} {catchDisconnect}", string.Empty),
            "DisposeCoreHelper" => ("await DisposeCore();", $"private async global::System.Threading.Tasks.ValueTask DisposeCore() {{ try {{ await JS.InvokeVoidAsync(\"app.x\"); }} {catchDisconnect} }}"),
            "DisposeAsyncCoreHelper" => ("await DisposeAsyncCore();", $"private async global::System.Threading.Tasks.ValueTask DisposeAsyncCore() {{ try {{ await JS.InvokeVoidAsync(\"app.x\"); }} {catchDisconnect} }}"),
            "OtherHelper" => ("await TearDownAsync();", $"private async global::System.Threading.Tasks.ValueTask TearDownAsync() {{ try {{ await JS.InvokeVoidAsync(\"app.x\"); }} {catchDisconnect} }}"),
            _ => ($"try {{ await {HttpCall}; }} {catchDisconnect}", string.Empty)
        };

        var ids = await IdsAsync($$"""
            [global::Microsoft.AspNetCore.Components.Inject] private global::Microsoft.JSInterop.IJSRuntime JS { get; set; } = default!;

            public async global::System.Threading.Tasks.ValueTask DisposeAsync()
            {
                {{disposeBody}}
            }

            {{helper}}
            """);

        Assert.Equal(expectsDiagnostic, ids.Overlaps(["NTBA0004", "NTBA0005"]));
    }

    // ---- 3. Source following ----

    private static async Task<bool> FollowsToIoAsync(string members, string statement, string services, IReadOnlyDictionary<string, string>? options = null)
    {
        var ids = await IdsAsync(
            $$"""
            {{members}}

            protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
            {
                {{statement}}
            }
            """,
            options,
            new SourceFile("Services/Services.cs", $$"""
                namespace TestComponents;

                {{services}}
                """));
        return ids.Contains("NTBA0003");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task InterfaceWithSeveralImplementations_IsNotFollowedAndFallsBackToNaming(bool hasSecondImplementation, bool expectsNtba0003)
    {
        var second = hasSecondImplementation
            ? "public sealed class OrderServiceB : IOrderService { public global::System.Threading.Tasks.Task LoadAsync() => global::System.Threading.Tasks.Task.CompletedTask; }"
            : string.Empty;

        var reported = await FollowsToIoAsync(
            "private global::TestComponents.IOrderService Svc { get; } = null!;",
            "await Svc.LoadAsync();",
            $$"""
            public interface IOrderService { global::System.Threading.Tasks.Task LoadAsync(); }
            public sealed class OrderServiceA : IOrderService { public global::System.Threading.Tasks.Task LoadAsync() => global::System.Threading.Tasks.Task.CompletedTask; }
            {{second}}
            """);

        Assert.Equal(expectsNtba0003, reported);
    }

    [Theory]
    [InlineData("IRepo<Foo>", "IRepo<T>", "FooRepo : IRepo<Foo>", "global::System.Threading.Tasks.Task.CompletedTask", false)]
    [InlineData("IFeed", "IFeed", "FeedImpl : IFeed", "{HTTP}", true)]
    [InlineData("IFeed", "IFeed", "FeedImpl : IFeed", "global::System.Threading.Tasks.Task.CompletedTask", false)]
    public async Task InterfaceWithSingleImplementation_IsFollowedAndJudgedByItsBody(string fieldType, string interfaceDeclaration, string implementation, string body, bool expectsNtba0003)
    {
        var reported = await FollowsToIoAsync(
            $"private global::TestComponents.{fieldType} Svc {{ get; }} = null!;",
            "await Svc.LoadAsync();",
            $$"""
            public sealed class Foo { }
            public interface {{interfaceDeclaration}} { global::System.Threading.Tasks.Task LoadAsync(); }
            public sealed class {{implementation}} { public global::System.Threading.Tasks.Task LoadAsync() => {{body.Replace("{HTTP}", HttpCall)}}; }
            """);

        Assert.Equal(expectsNtba0003, reported);
    }

    [Fact]
    public async Task GenericInterfaceImplementedByClosedClass_IsFollowed()
    {
        var reported = await FollowsToIoAsync(
            "private global::TestComponents.IRepo<global::TestComponents.Foo> Svc { get; } = null!;",
            "await Svc.LoadAsync();",
            $$"""
            public sealed class Foo { }
            public interface IRepo<T> { global::System.Threading.Tasks.Task LoadAsync(); }
            public sealed class FooRepo : IRepo<Foo> { public global::System.Threading.Tasks.Task LoadAsync() => {{HttpCall}}; }
            """);

        Assert.True(reported);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task LocalFunctionCall_IsJudgedByTheLocalFunctionBody(bool localFunctionDoesIo, bool expectsNtba0003)
    {
        var body = localFunctionDoesIo ? $"await {HttpCall};" : "await global::System.Threading.Tasks.Task.CompletedTask;";

        var reported = await FollowsToIoAsync(
            string.Empty,
            $$"""
            async global::System.Threading.Tasks.Task FetchAsync()
            {
                {{body}}
            }

            await FetchAsync();
            """,
            "public sealed class Unused { }");

        Assert.Equal(expectsNtba0003, reported);
    }

    [Theory]
    [InlineData("abstract", false)]
    [InlineData("concrete", true)]
    public async Task AbstractMethodWithoutBody_IsNotFollowedThroughAnOverride(string callTarget, bool expectsNtba0003)
    {
        var reported = await FollowsToIoAsync(
            callTarget == "abstract"
                ? "private global::TestComponents.OrderBase Svc { get; } = null!;"
                : "private global::TestComponents.OrderImpl Svc { get; } = null!;",
            "await Svc.RunAsync();",
            $$"""
            public abstract class OrderBase { public abstract global::System.Threading.Tasks.Task RunAsync(); }
            public sealed class OrderImpl : OrderBase { public override async global::System.Threading.Tasks.Task RunAsync() => await {{HttpCall}}; }
            """);

        Assert.Equal(expectsNtba0003, reported);
    }

    [Theory]
    [InlineData("none", false)]
    [InlineData("Alpha", true)]
    [InlineData("Beta", false)]
    public async Task MutuallyRecursiveProjectClasses_TerminateWithAStableOneLevelVerdict(string ioIn, bool expectsNtba0003)
    {
        var io = $"await {HttpCall};";
        var sources = new[]
        {
            TestComponentSources.CreateInteractiveComponent("Loader", EmptyRenderTree, """
                private global::TestComponents.Alpha Svc { get; } = new();

                protected override async global::System.Threading.Tasks.Task OnInitializedAsync() => await Svc.PingAsync();
                """),
            new SourceFile("Services/Pair.cs", $$"""
                namespace TestComponents;

                public sealed class Alpha
                {
                    public Beta? Other { get; set; }
                    public async global::System.Threading.Tasks.Task PingAsync()
                    {
                        if (Other is not null) { await Other.PongAsync(); }
                        {{(ioIn == "Alpha" ? io : string.Empty)}}
                    }
                }

                public sealed class Beta
                {
                    public Alpha? Other { get; set; }
                    public async global::System.Threading.Tasks.Task PongAsync()
                    {
                        if (Other is not null) { await Other.PingAsync(); }
                        {{(ioIn == "Beta" ? io : string.Empty)}}
                    }
                }
                """)
        };

        var concurrent = await AnalyzerTestHarness.GetDiagnosticsAsync(sources, concurrentAnalysis: true);
        var serial = await AnalyzerTestHarness.GetDiagnosticsAsync(sources, concurrentAnalysis: false);

        Assert.Equal(expectsNtba0003, Ids(concurrent).Contains("NTBA0003"));
        Assert.Equal(concurrent.Select(static d => d.Id), serial.Select(static d => d.Id));
    }

    [Theory]
    [InlineData("Alpha", false)]
    [InlineData("Beta", true)]
    public async Task FollowingStopsAfterOneLevel(string calledType, bool expectsNtba0003)
    {
        var reported = await FollowsToIoAsync(
            $"private global::TestComponents.{calledType} Svc {{ get; }} = new();",
            calledType == "Alpha" ? "await Svc.RunAsync();" : "await Svc.FetchAsync();",
            $$"""
            public sealed class Alpha { public async global::System.Threading.Tasks.Task RunAsync() => await new Beta().FetchAsync(); }
            public sealed class Beta { public async global::System.Threading.Tasks.Task FetchAsync() => await {{HttpCall}}; }
            """);

        Assert.Equal(expectsNtba0003, reported);
    }

    // ---- 4. Known I/O APIs ----

    [Theory]
    [InlineData("_ = global::System.IO.File.ReadAllText(\"a.txt\");", true)]
    [InlineData("await Data.ReadAsync(new byte[1]);", true)]
    [InlineData("await Ms.ReadAsync(new byte[1]);", true)]
    [InlineData("_ = global::System.Text.Json.JsonSerializer.Deserialize<int>(\"1\");", true)]
    [InlineData("_ = await Content.ReadAsStringAsync();", true)]
    [InlineData("_ = await global::System.Net.Http.Json.HttpClientJsonExtensions.GetFromJsonAsync<int>(Http, \"x\");", true)]
    [InlineData("await Context.SaveChangesAsync();", true)]
    [InlineData("await Factory.CreateDbContextAsync();", false)]
    [InlineData("Context.Dispose();", false)]
    [InlineData("Ms.Dispose();", false)]
    public async Task KnownIoApis_CountAsFailureProneExceptContextCreationAndDisposal(string statement, bool expectsNtba0003)
    {
        var ids = await IdsAsync(
            $$"""
            private global::System.Net.Http.HttpClient Http { get; } = null!;
            private global::System.Net.Http.HttpContent Content { get; } = null!;
            private global::System.IO.Stream Data { get; } = null!;
            private global::System.IO.MemoryStream Ms { get; } = null!;
            private global::Microsoft.EntityFrameworkCore.IDbContextFactory Factory { get; } = null!;
            private global::Microsoft.EntityFrameworkCore.DbContext Context { get; } = null!;

            protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
            {
                {{statement}}
                await global::System.Threading.Tasks.Task.CompletedTask;
            }
            """,
            null,
            new SourceFile("EfCore.cs", """
                namespace Microsoft.EntityFrameworkCore;

                public interface IDbContextFactory
                {
                    global::System.Threading.Tasks.Task<DbContext> CreateDbContextAsync();
                }

                public abstract class DbContext : global::System.IDisposable
                {
                    public abstract global::System.Threading.Tasks.Task<int> SaveChangesAsync();
                    public abstract void Dispose();
                }
                """));

        Assert.Equal(expectsNtba0003, ids.Contains("NTBA0003"));
    }

    // ---- 5. Owner coverage ----

    private const string ChildSource = """
        __builder.OpenElement(0, "button");
        __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
        __builder.CloseElement();
        """;

    private static IEnumerable<SourceFile> Owner(string name, bool wrapsChild) =>
    [
        TestComponentSources.CreateInteractiveComponent(name, wrapsChild
            ? """
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
                """
            : """
                __builder.OpenComponent<global::TestComponents.Child>(0);
                __builder.CloseComponent();
                """)
    ];

    private static AdditionalSourceFile OwnerMarkup(string name, bool wrapsChild) =>
        TestComponentSources.CreateRazorMarkup(name, wrapsChild
            ? """
                @rendermode InteractiveServer
                <ErrorBoundary>
                    <Child />
                    <ErrorContent><p>Error</p></ErrorContent>
                </ErrorBoundary>
                """
            : """
                @rendermode InteractiveServer
                <Child />
                """);

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    public async Task ChildRenderedByTwoOwners_IsCoveredOnlyWhenEveryOwnerWrapsIt(bool firstWraps, bool secondWraps, bool expectsNtba0001)
    {
        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            sources:
            [
                TestComponentSources.CreateInteractiveComponent("Child", ChildSource, "private void HandleClick() => throw new global::System.InvalidOperationException();"),
                .. Owner("FirstOwner", firstWraps),
                .. Owner("SecondOwner", secondWraps)
            ],
            additionalFiles: [OwnerMarkup("FirstOwner", firstWraps), OwnerMarkup("SecondOwner", secondWraps)]);

        var ntba0001 = diagnostics.Where(static diagnostic => diagnostic.Id == "NTBA0001").ToArray();
        Assert.Equal(expectsNtba0001, ntba0001.Length > 0);
        Assert.All(ntba0001, static diagnostic => Assert.Contains("Child", diagnostic.Location.GetLineSpan().Path));
    }

    // ---- 6. Static SSR ----

    private static IEnumerable<SourceFile> RoutesApp(bool globallyInteractive, string routesTypeName = "Routes", string openedType = "global::TestComponents.Routes") =>
    [
        TestComponentSources.CreateStaticComponent(routesTypeName, EmptyRenderTree),
        TestComponentSources.CreateStaticComponent("App", $$"""
            __builder.OpenComponent<{{openedType}}>(0);
            {{(globallyInteractive ? "__builder.AddComponentRenderMode(global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer);" : string.Empty)}}
            __builder.CloseComponent();
            """)
    ];

    private static SourceFile Page(string name) => new($"Components/{name}Route.cs", $$"""
        namespace TestComponents;

        [global::Microsoft.AspNetCore.Components.Route("/{{name}}")]
        public partial class {{name}} : global::System.IDisposable
        {
        }
        """);

    private static SourceFile LifecycleAndDisposeWork(string name) => TestComponentSources.CreateStaticComponent(
        name,
        EmptyRenderTree,
        $$"""
        private global::System.Net.Http.HttpClient Http { get; } = new();

        protected override async global::System.Threading.Tasks.Task OnInitializedAsync() => await Http.GetStringAsync("https://example.test");

        public void Dispose() { _ = Http.GetStringAsync("https://example.test"); }
        """);

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task RoutablePageWithoutRenderMode_ReportsLifecycleAndDisposeOnlyWhenRoutesAreGloballyInteractive(bool globallyInteractive, bool expectsDiagnostics)
    {
        var ids = await IdsAsync([LifecycleAndDisposeWork("Dashboard"), Page("Dashboard"), .. RoutesApp(globallyInteractive)]);

        Assert.Equal(expectsDiagnostics, ids.Contains("NTBA0003"));
        Assert.Equal(expectsDiagnostics, ids.Contains("NTBA0004"));
    }

    [Theory]
    [InlineData("[global::Microsoft.AspNetCore.Components.CascadingParameter] public global::Microsoft.AspNetCore.Http.HttpContext? Ctx { get; set; }", false, false)]
    [InlineData("public global::Microsoft.AspNetCore.Http.HttpContext? Ctx { get; set; }", false, true)]
    [InlineData("", false, true)]
    [InlineData("[global::Microsoft.AspNetCore.Components.CascadingParameter] public global::Microsoft.AspNetCore.Http.HttpContext? Ctx { get; set; }", true, true)]
    public async Task CascadingHttpContext_MarksOnlyComponentsWithoutRenderModeAsStaticSsr(string httpContextProperty, bool interactive, bool expectsNtba0003)
    {
        var members = $$"""
            {{httpContextProperty}}

            protected override async global::System.Threading.Tasks.Task OnInitializedAsync() => await new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test");
            """;
        var component = interactive
            ? TestComponentSources.CreateInteractiveComponent("Widget", EmptyRenderTree, members)
            : TestComponentSources.CreateStaticComponent("Widget", EmptyRenderTree, members);

        var ids = await IdsAsync([component], null, NoOwner);

        Assert.Equal(expectsNtba0003, ids.Contains("NTBA0003"));
    }

    // ---- 7. Routes component detection ----

    [Theory]
    [InlineData("global::Microsoft.AspNetCore.Components.Routing.Router", true, true)]
    [InlineData("global::Microsoft.AspNetCore.Components.Routing.Router", false, false)]
    [InlineData("global::TestComponents.Shell", true, false)]
    public async Task RouterOpenedWithRenderMode_MakesRoutablePagesInteractive(string openedType, bool withRenderMode, bool expectsNtba0003)
    {
        var ids = await IdsAsync([
            LifecycleAndDisposeWork("Dashboard"),
            Page("Dashboard"),
            .. RoutesApp(withRenderMode, routesTypeName: "Shell", openedType: openedType)]);

        Assert.Equal(expectsNtba0003, ids.Contains("NTBA0003"));
    }

    // ---- 8. Awaiting a stored task ----

    [Theory]
    [InlineData("await _loadTask;", null, false)]
    [InlineData("await _loadTask;", "true", true)]
    [InlineData("await global::System.Threading.Tasks.Task.CompletedTask;", "true", false)]
    public async Task AwaitOfStoredTask_IsFailureProneOnlyInStrictMode(string statement, string? strict, bool expectsNtba0003)
    {
        var ids = await IdsAsync(
            $$"""
            private global::System.Threading.Tasks.Task _loadTask = null!;

            protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
            {
                {{statement}}
            }
            """,
            strict is null ? null : new Dictionary<string, string> { ["ntba.unknown_awaits_are_failure_prone"] = strict });

        Assert.Equal(expectsNtba0003, ids.Contains("NTBA0003"));
    }

    // ---- 9. Boundary fallback UI from referenced assemblies ----

    [Theory]
    [InlineData("global::Lib.OwnUiBoundary", false)]
    [InlineData("global::Lib.PlainBoundary", true)]
    [InlineData("global::Microsoft.AspNetCore.Components.Web.ErrorBoundary", true)]
    public async Task RootBoundaryFromReferencedAssembly_ReportsMissingErrorContentOnlyWhenItUsesTheDefaultFallback(string boundaryType, bool expectsNtba0009)
    {
        var platformReferences = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(static path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray();
        var library = CSharpCompilation.Create(
            "Lib",
            [CSharpSyntaxTree.ParseText("""
                namespace Lib;

                public class OwnUiBoundary : global::Microsoft.AspNetCore.Components.Web.ErrorBoundary
                {
                    protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) => builder.AddContent(0, "own fallback");
                }

                public class PlainBoundary : global::Microsoft.AspNetCore.Components.Web.ErrorBoundary
                {
                }
                """, cancellationToken: TestContext.Current.CancellationToken)],
            platformReferences,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = new MemoryStream();
        var emitted = library.Emit(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));

        var component = TestComponentSources.CreateInteractiveComponent("RootComponent", $$"""
            __builder.OpenComponent<{{boundaryType}}>(0);
            __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
            {
                __builder2.OpenElement(2, "div");
                __builder2.CloseElement();
            }));
            __builder.CloseComponent();
            """);
        var compilation = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText(component.Text, new CSharpParseOptions(LanguageVersion.Preview), component.Path, cancellationToken: TestContext.Current.CancellationToken)],
            [.. platformReferences, MetadataReference.CreateFromImage(stream.ToArray())],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var options = new AnalyzerOptions([], new InMemoryAnalyzerConfigOptionsProvider(new Dictionary<string, string>()));
        var diagnostics = await compilation
            .WithAnalyzers([new BlazorErrorHandlingAnalyzer()], new CompilationWithAnalyzersOptions(options, null, true, false))
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "AD0001");
        Assert.Equal(expectsNtba0009, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0009"));
    }
}
