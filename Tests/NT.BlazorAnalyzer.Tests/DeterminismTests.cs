using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

/// <summary>
/// Results must not depend on thread scheduling, source order, additional-file order, malformed IDE input, or a cancelled earlier run.
/// The sequential and reordered runs are the deterministic lever; the repeated concurrent runs only raise the odds of exposing a race.
/// </summary>
public sealed class DeterminismTests
{
    private const int ConcurrentRepetitions = 5;
    private const int MaxRotations = 6;

    private const string ButtonRenderTree = """
        __builder.OpenElement(0, "button");
        __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
        __builder.CloseElement();
        """;

    private const string EmptyRenderTree = """
        __builder.OpenElement(0, "div");
        __builder.CloseElement();
        """;

    private static readonly string[] FixtureNameList =
    [
        "NestedCustomBoundaryWithRazorMarkup",
        "OwnerCoverageWithTwoOwners",
        "ErrorBoundaryBaseSubclassWithSecondOwner",
        "DerivedBoundaryRootWithConditionalContent",
        "InteractiveRegionsInsideInertHtmlRoot",
        "ResultTypeAndSourceFollowing",
        "BackgroundTasks",
        "RazorLocationsSharingTheMarkupCache",
        "CustomBoundaryRootLocatedInMarkup",
        "ManyOwnerGroupsWithMarkup",
    ];

    private static readonly string[] MalformedCaseNameList =
    [
        "UnclosedMethodBody",
        "ExpressionBodiedBuildRenderTree",
        "BodylessBuildRenderTree",
        "MissingBaseType",
        "CyclicBaseTypes",
        "FailedOverloadResolutionInHandler",
        "FailedOverloadResolutionInEventCallback",
        "DuplicateBuildRenderTreeOverride",
        "RazorUnterminatedCodeBlock",
        "RazorUnterminatedHtmlComment",
        "RazorUnterminatedRazorComment",
        "RazorUnterminatedQuotedAttribute",
        "RazorStrayErrorBoundaryCloseTag",
        "RazorTagEndingInDot",
        "RazorTagThatIsOnlyADot",
    ];

    public static TheoryData<string> FixtureNames => ToTheoryData(FixtureNameList);

    public static TheoryData<string> MalformedCaseNames => ToTheoryData(MalformedCaseNameList);

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public async Task Diagnostics_AreIdenticalAcrossConcurrencyAndInputOrder(string fixtureName)
    {
        var fixture = GetFixture(fixtureName);
        var baseline = await AnalyzeAsync(fixture.Sources, fixture.AdditionalFiles, concurrentAnalysis: false);

        // Pins each fixture to what its source test proves, so an equivalence over an empty or broken result cannot pass vacuously.
        Assert.True(
            fixture.ExpectedIds.SequenceEqual(baseline.Select(static key => key[..key.IndexOf(' ')])),
            $"Baseline for {fixtureName} does not match [{string.Join(", ", fixture.ExpectedIds)}]:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", baseline)}");

        var reversedSources = fixture.Sources.Reverse().ToArray();
        var reversedAdditionalFiles = fixture.AdditionalFiles.Reverse().ToArray();
        foreach (var offset in GetRotationOffsets(fixture.Sources.Length))
        {
            var rotated = fixture.Sources.Skip(offset).Concat(fixture.Sources.Take(offset)).ToArray();
            AssertSameDiagnostics(baseline, await AnalyzeAsync(rotated, fixture.AdditionalFiles, concurrentAnalysis: false), $"sequential, sources rotated by {offset}");
        }

        AssertSameDiagnostics(baseline, await AnalyzeAsync(reversedSources, fixture.AdditionalFiles, concurrentAnalysis: false), "sequential, sources reversed");
        AssertSameDiagnostics(baseline, await AnalyzeAsync(fixture.Sources, reversedAdditionalFiles, concurrentAnalysis: false), "sequential, additional files reversed");
        AssertSameDiagnostics(baseline, await AnalyzeAsync(reversedSources, reversedAdditionalFiles, concurrentAnalysis: false), "sequential, sources and additional files reversed");

        for (var run = 1; run <= ConcurrentRepetitions; run++)
        {
            AssertSameDiagnostics(baseline, await AnalyzeAsync(fixture.Sources, fixture.AdditionalFiles, concurrentAnalysis: true), $"concurrent run {run}, given order");
            AssertSameDiagnostics(baseline, await AnalyzeAsync(reversedSources, reversedAdditionalFiles, concurrentAnalysis: true), $"concurrent run {run}, everything reversed");
        }
    }

    [Theory]
    [MemberData(nameof(MalformedCaseNames))]
    public async Task MalformedIdeInput_CompletesWithoutAnalyzerException_AndKeepsIntactDiagnostics(string caseName)
    {
        var malformed = GetMalformedCase(caseName);
        if (malformed.CompilerError is { } compilerError)
        {
            // Proves the fixture really is the broken IDE state it claims to be, not code the compiler accepts.
            Assert.Contains(CreateCompilation(malformed.Sources).GetDiagnostics(TestContext.Current.CancellationToken), diagnostic => diagnostic.Id == compilerError);
        }

        var intactSources = new[] { IntactCounterSource };
        var intactMarkup = new[] { IntactCounterMarkup };
        var intactOnly = await AnalyzeAsync(intactSources, intactMarkup, concurrentAnalysis: false);
        Assert.Contains(intactOnly, static key => key.StartsWith("NTBA0001 ", StringComparison.Ordinal));

        foreach (var concurrentAnalysis in new[] { false, true })
        {
            // The harness fails the run on AD0001, which is how an analyzer exception on malformed code surfaces.
            var diagnostics = await AnalyzeAsync([.. malformed.Sources, .. intactSources], [.. malformed.AdditionalFiles, .. intactMarkup], concurrentAnalysis);
            var mode = concurrentAnalysis ? "concurrent" : "sequential";

            AssertSameDiagnostics(intactOnly, diagnostics.Where(static key => key.Contains("IntactCounter", StringComparison.Ordinal)).ToArray(), $"{mode}, intact component next to {caseName}");
            if (malformed.ExpectedComponentDiagnostic is { } expected)
            {
                Assert.Contains(diagnostics, key => key.StartsWith(expected.Id + " ", StringComparison.Ordinal) && key.Contains(expected.Component, StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public async Task CancelledRun_DoesNotChangeTheResultsOfALaterRunOnTheSameCompilation()
    {
        var (sources, markup) = CreateOwnerGroups(groupCount: 20);
        var compilation = CreateCompilation(sources);
        var options = CreateAnalyzerOptions(markup);
        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(new BlazorErrorHandlingAnalyzer(), new BackgroundFailureAnalyzer());
        var clean = await RunAsync(CreateCompilationWithAnalyzers(compilation, analyzers, options), TestContext.Current.CancellationToken);
        Assert.Equal(40, clean.Count(static key => key.StartsWith("NTBA0001 ", StringComparison.Ordinal)));

        var cancelledRuns = 0;
        foreach (var delayMilliseconds in new[] { 0, 1, 3, 10, 30, 100 })
        {
            using var cancellation = new CancellationTokenSource();
            if (delayMilliseconds == 0)
            {
                cancellation.Cancel();
            }
            else
            {
                cancellation.CancelAfter(TimeSpan.FromMilliseconds(delayMilliseconds));
            }

            var interrupted = CreateCompilationWithAnalyzers(compilation, analyzers, options);
            string[]? completed = null;
            var exception = await Record.ExceptionAsync(async () => completed = await RunAsync(interrupted, cancellation.Token));

            if (exception is null)
            {
                AssertSameDiagnostics(clean, completed!, $"run allowed {delayMilliseconds} ms that completed before cancellation");
            }
            else
            {
                Assert.IsAssignableFrom<OperationCanceledException>(exception);
                cancelledRuns++;
            }

            TestContext.Current.TestOutputHelper?.WriteLine($"Cancel after {delayMilliseconds} ms: {(exception is null ? "completed" : "cancelled")}");

            AssertSameDiagnostics(clean, await RunAsync(interrupted, TestContext.Current.CancellationToken), $"same CompilationWithAnalyzers resumed after cancel at {delayMilliseconds} ms");
            AssertSameDiagnostics(clean, await RunAsync(CreateCompilationWithAnalyzers(compilation, analyzers, options), TestContext.Current.CancellationToken), $"fresh CompilationWithAnalyzers after cancel at {delayMilliseconds} ms");
        }

        // A zero delay cancels before analysis starts, so at least one run is guaranteed to observe cancellation.
        Assert.True(cancelledRuns >= 1, "No run observed cancellation.");
    }

    private static async Task<string[]> AnalyzeAsync(IEnumerable<SourceFile> sources, IEnumerable<AdditionalSourceFile> additionalFiles, bool concurrentAnalysis) =>
        ToKeys(await AnalyzerTestHarness.GetDiagnosticsAsync(sources, additionalFiles, concurrentAnalysis: concurrentAnalysis));

    private static async Task<string[]> RunAsync(CompilationWithAnalyzers compilationWithAnalyzers, CancellationToken cancellationToken)
    {
        var diagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);
        Assert.DoesNotContain(diagnostics, static diagnostic => diagnostic.Id == "AD0001");
        return ToKeys(diagnostics);
    }

    private static string[] ToKeys(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics
            .Select(static diagnostic =>
            {
                var span = diagnostic.Location.GetLineSpan();
                return $"{diagnostic.Id} {span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): {diagnostic.GetMessage(CultureInfo.InvariantCulture)}";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static void AssertSameDiagnostics(string[] expected, string[] actual, string variant)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
        {
            Assert.Fail($"Diagnostics differ for {variant}.{Environment.NewLine}Expected:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", expected)}{Environment.NewLine}Actual:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", actual)}");
        }
    }

    private static IEnumerable<int> GetRotationOffsets(int count)
    {
        var step = Math.Max(1, count / MaxRotations);
        for (var offset = step; offset < count; offset += step)
        {
            yield return offset;
        }
    }

    private static TheoryData<string> ToTheoryData(IEnumerable<string> values)
    {
        var data = new TheoryData<string>();
        foreach (var value in values)
        {
            data.Add(value);
        }

        return data;
    }

    private static CompilationWithAnalyzers CreateCompilationWithAnalyzers(Compilation compilation, ImmutableArray<DiagnosticAnalyzer> analyzers, AnalyzerOptions options) =>
        compilation.WithAnalyzers(analyzers, new CompilationWithAnalyzersOptions(options, null, concurrentAnalysis: true, logAnalyzerExecutionTime: false, reportSuppressedDiagnostics: false));

    private static AnalyzerOptions CreateAnalyzerOptions(IEnumerable<AdditionalSourceFile> additionalFiles) =>
        new(
            additionalFiles.Select(static file => (AdditionalText)new InMemoryAdditionalText(file.Path, file.Text)).ToImmutableArray(),
            new InMemoryAnalyzerConfigOptionsProvider(new Dictionary<string, string>()));

    private static CSharpCompilation CreateCompilation(IEnumerable<SourceFile> sources)
    {
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);
        var references = new Dictionary<string, MetadataReference>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string trustedPlatformAssemblies)
        {
            foreach (var path in trustedPlatformAssemblies.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                references[path] = MetadataReference.CreateFromFile(path);
            }
        }

        foreach (var assembly in new[] { typeof(Microsoft.AspNetCore.Components.ComponentBase).Assembly, typeof(Microsoft.AspNetCore.Components.Web.ErrorBoundary).Assembly })
        {
            references[assembly.Location] = MetadataReference.CreateFromFile(assembly.Location);
        }

        return CSharpCompilation.Create(
            assemblyName: "NT.BlazorAnalyzer.Tests.Determinism",
            syntaxTrees: sources.Select(source => CSharpSyntaxTree.ParseText(SourceText.From(source.Text), parseOptions, source.Path)),
            references: references.Values,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static Fixture GetFixture(string name) => name switch
    {
        "NestedCustomBoundaryWithRazorMarkup" => new(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Parent",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::TestComponents.Section>(0);
                        __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) => {
                            __builder2.OpenElement(2, "div");
                            __builder2.OpenComponent<global::TestComponents.AppBoundary>(3);
                            __builder2.AddAttribute(4, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder3) => {
                                foreach (var item in new[] { 1, 2 }) {
                                    __builder3.OpenComponent<global::TestComponents.Child>(5);
                                    __builder3.AddComponentParameter(6, "Value", item);
                                    __builder3.SetKey(item);
                                    __builder3.CloseComponent();
                                }
                            }));
                            __builder2.CloseComponent();
                            __builder2.CloseElement();
                        }));
                        __builder.CloseComponent();
                        """),
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Child",
                    renderTreeStatements: ButtonRenderTree,
                    razorMethods: """
                        [global::Microsoft.AspNetCore.Components.Parameter] public int Value { get; set; }

                        private void HandleClick() => throw new global::System.InvalidOperationException();
                        """),
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Outside",
                    renderTreeStatements: ButtonRenderTree,
                    razorMethods: "private void HandleClick() { }"),
                AppBoundary,
                new SourceFile("Components/Section.cs", """
                    namespace TestComponents;

                    public class Section : global::Microsoft.AspNetCore.Components.ComponentBase
                    {
                        [global::Microsoft.AspNetCore.Components.Parameter] public global::Microsoft.AspNetCore.Components.RenderFragment? ChildContent { get; set; }
                    }
                    """),
            ],
            [
                TestComponentSources.CreateRazorMarkup("Parent", """
                    @rendermode InteractiveServer
                    <Section>
                        <div>
                            <AppBoundary>
                                @foreach (var item in new[] { 1, 2 }) {
                                    <Child @key="item" Value="item" />
                                }
                            </AppBoundary>
                        </div>
                    </Section>
                    """),
                TestComponentSources.CreateRazorMarkup("Outside", """
                    @rendermode InteractiveServer
                    <button @onclick="HandleClick">Click</button>
                    """),
            ],
            ["NTBA0001"]),
        "OwnerCoverageWithTwoOwners" => new(
            [
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
                    razorMethods: "private void HandleParent() { }"),
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Child",
                    renderTreeStatements: ButtonRenderTree,
                    razorMethods: "private void HandleClick() { }"),
            ],
            [
                TestComponentSources.CreateRazorMarkup("CoveredParent", """
                    @rendermode InteractiveServer
                    <ErrorBoundary>
                        <ChildContent>
                            <Child />
                        </ChildContent>
                        <ErrorContent>
                            <p></p>
                        </ErrorContent>
                    </ErrorBoundary>
                    """),
                TestComponentSources.CreateRazorMarkup("UncoveredParent", """
                    @rendermode InteractiveServer
                    <div>
                        <button @onclick="HandleParent"></button>
                        <Child />
                    </div>
                    """),
                TestComponentSources.CreateRazorMarkup("Child", """
                    @rendermode InteractiveServer
                    <button @onclick="HandleClick"></button>
                    """),
            ],
            ["NTBA0001", "NTBA0001"]),
        "ErrorBoundaryBaseSubclassWithSecondOwner" => new(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Parent",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::TestComponents.AppBoundary>(0);
                        __builder.AddComponentParameter(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                        {
                            foreach (var item in new[] { 1, 2 })
                            {
                                __builder2.OpenComponent<global::TestComponents.Child>(2);
                                __builder2.CloseComponent();
                            }
                        }));
                        __builder.CloseComponent();
                        """),
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Child",
                    renderTreeStatements: ButtonRenderTree,
                    razorMethods: "private void HandleClick() => throw new global::System.InvalidOperationException();"),
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "SecondOwner",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::TestComponents.Child>(0);
                        __builder.CloseComponent();
                        """),
                AppBoundary,
            ],
            [],
            ["NTBA0001", "NTBA0002"]),
        "DerivedBoundaryRootWithConditionalContent" => new(
            [
                TestComponentSources.CreateCustomBoundary("IntersectErrorBoundary"),
                TestComponentSources.CreateInteractiveComponent(componentName: "PaginationButtonsWithCount", renderTreeStatements: EmptyRenderTree),
                new SourceFile("Components/DashboardItem.cs", """
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
                new SourceFile("Components/TnTDataGrid.razor.g.cs", """
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
                        """),
            ],
            [
                TestComponentSources.CreateRazorMarkup("UpcomingAndPastDueFollowUp", """
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
                    """),
            ],
            ["NTBA0009"]),
        "InteractiveRegionsInsideInertHtmlRoot" => new(
            [
                TestComponentSources.CreateStaticComponent(componentName: "PageHeader", renderTreeStatements: EmptyRenderTree),
                new SourceFile("Components/TnTInputText.razor.g.cs", """
                    namespace TestComponents
                    {
                        public partial class TnTInputText : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            [global::Microsoft.AspNetCore.Components.Parameter]
                            public string? Value { get; set; }

                            [global::Microsoft.AspNetCore.Components.Parameter]
                            public global::Microsoft.AspNetCore.Components.EventCallback<string?> ValueChanged { get; set; }

                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
                                __builder.OpenElement(0, "input");
                                __builder.CloseElement();
                            }
                        }
                    }
                    """),
                new SourceFile("Components/TnTDataGrid.razor.g.cs", """
                    namespace TestComponents
                    {
                        public partial class TnTDataGrid : global::Microsoft.AspNetCore.Components.ComponentBase
                        {
                            [global::Microsoft.AspNetCore.Components.Parameter]
                            public global::Microsoft.AspNetCore.Components.EventCallback<string> OnRowClicked { get; set; }

                            protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                            {
                                __builder.OpenElement(0, "table");
                                __builder.CloseElement();
                            }
                        }
                    }
                    """),
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Roles",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::TestComponents.PageHeader>(0);
                        __builder.CloseComponent();

                        __builder.OpenElement(3, "div");
                        __builder.OpenElement(4, "div");
                        __builder.OpenComponent<global::TestComponents.TnTInputText>(5);
                        __builder.AddComponentParameter(6, "Value", global::Microsoft.AspNetCore.Components.CompilerServices.RuntimeHelpers.TypeCheck<string?>(_filter));
                        __builder.AddComponentParameter(7, "ValueChanged", global::Microsoft.AspNetCore.Components.CompilerServices.RuntimeHelpers.TypeCheck<global::Microsoft.AspNetCore.Components.EventCallback<string?>>(global::Microsoft.AspNetCore.Components.EventCallback.Factory.CreateBinder<string?>(this, __value => _filter = __value, _filter)));
                        __builder.CloseComponent();
                        __builder.CloseElement();

                        __builder.OpenComponent<global::TestComponents.TnTDataGrid>(8);
                        __builder.AddComponentParameter(9, "OnRowClicked", global::Microsoft.AspNetCore.Components.CompilerServices.RuntimeHelpers.TypeCheck<global::Microsoft.AspNetCore.Components.EventCallback<string>>(global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<string>(this, value => OpenRole(value))));
                        __builder.CloseComponent();
                        __builder.CloseElement();
                        """,
                    razorMethods: """
                        private string? _filter;

                        private void OpenRole(string role)
                        {
                        }
                        """),
            ],
            [
                TestComponentSources.CreateRazorMarkup("Roles", """
                    @rendermode InteractiveServer

                    <PageHeader Title="Roles" />

                    <div class="roles">
                        <div class="filters">
                            <TnTInputText @bind-Value="_filter" />
                        </div>

                        <TnTDataGrid OnRowClicked="@((string value) => OpenRole(value))" />
                    </div>
                    """),
            ],
            ["NTBA0001", "NTBA0001"]),
        "ResultTypeAndSourceFollowing" => new(
            [
                CreateResultLoader("Loader", "public sealed class Result { public bool IsSuccessStatusCode { get; init; } }"),
                CreateResultLoader("SafeLoader", "public sealed class Result { public bool IsSuccess { get; init; } }"),
                .. CreateFeatureGate("FeatureGate", "FeatureService", "public async global::System.Threading.Tasks.Task<bool> IsEnabledAsync() => (await Http.GetStringAsync(\"flags\")).Length > 0;"),
                .. CreateFeatureGate("SafeFeatureGate", "SafeFeatureService", "public async global::System.Threading.Tasks.Task<bool> IsEnabledAsync() => (await External.FetchAsync()).Length > 0;"),
            ],
            [],
            ["NTBA0003", "NTBA0003"]),
        "BackgroundTasks" => new(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Worker",
                    renderTreeStatements: EmptyRenderTree,
                    razorMethods: """
                        private DataService Service { get; } = new();
                        private readonly global::System.Timers.Timer timer = new();
                        private global::System.Threading.Timer? threadingTimer;

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

                        private void StartUnobserved() { _ = Service.LoadAsync(); }
                        private void StartUnobservedWrapper() { _ = UnsafeAsync(); }
                        private void StartSafe() { _ = SafeAsync(); }
                        private void HookTimer() => timer.Elapsed += OnElapsed;
                        private async void OnElapsed(object? sender, global::System.Timers.ElapsedEventArgs args) => await Service.LoadAsync();
                        private void HookEvent() => Service.Changed += (_, _) => Service.Load();
                        private void StartThreadingTimer() => threadingTimer = new global::System.Threading.Timer(_ => Service.Load(), null, 0, 1000);

                        public sealed class DataService
                        {
                            public global::System.Threading.Tasks.Task LoadAsync() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data");

                            public void Load() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data").Wait();

                            public event global::System.EventHandler? Changed;
                        }
                        """),
            ],
            [],
            ["NTBA0007", "NTBA0011", "NTBA0011", "NTBA0012", "NTBA0012", "NTBA0012"]),
        "RazorLocationsSharingTheMarkupCache" => new(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "MarkupCounter",
                    renderTreeStatements: ButtonRenderTree,
                    razorMethods: "private void HandleClick() { }"),
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
                    razorMethods: "private void HandleClick() { }"),
            ],
            [
                TestComponentSources.CreateRazorMarkup("MarkupCounter", """
                    @rendermode InteractiveServer
                    <button @onclick="HandleClick">Click</button>
                    """),
                TestComponentSources.CreateRazorMarkup("BoundaryMarkupCounter", """
                    @rendermode InteractiveServer
                    <ErrorBoundary>
                        <button @onclick="HandleClick">Click</button>
                    </ErrorBoundary>
                    """),
            ],
            ["NTBA0001", "NTBA0009"]),
        // The boundary is recognized in markup only by its declared name, so the reported location depends on every markup reader agreeing on the boundary set.
        "CustomBoundaryRootLocatedInMarkup" => new(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Page",
                    renderTreeStatements: """
                        __builder.OpenComponent<global::TestComponents.PageBoundary>(0);
                        __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                        {
                            __builder2.OpenElement(2, "button");
                            __builder2.AddAttribute(3, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                            __builder2.CloseElement();
                        }));
                        __builder.CloseComponent();
                        """,
                    razorMethods: "private void HandleClick() { }"),
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Sibling",
                    renderTreeStatements: ButtonRenderTree,
                    razorMethods: "private void HandleClick() { }"),
                TestComponentSources.CreateCustomBoundary("PageBoundary"),
            ],
            [
                TestComponentSources.CreateRazorMarkup("Page", """
                    @rendermode InteractiveServer

                    <PageBoundary>
                        <button @onclick="HandleClick">Click</button>
                    </PageBoundary>
                    """),
                TestComponentSources.CreateRazorMarkup("Sibling", """
                    @rendermode InteractiveServer
                    <button @onclick="HandleClick">Click</button>
                    """),
            ],
            ["NTBA0001", "NTBA0009"]),
        "ManyOwnerGroupsWithMarkup" => CreateOwnerGroupsFixture(groupCount: 8),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown fixture."),
    };

    private static Fixture CreateOwnerGroupsFixture(int groupCount)
    {
        var (sources, markup) = CreateOwnerGroups(groupCount);
        return new(sources, markup, Enumerable.Repeat("NTBA0001", groupCount * 2).ToArray());
    }

    // Each group repeats the mixed-owner shape: one owner wraps the child in an ErrorBoundary, the other does not.
    private static (SourceFile[] Sources, AdditionalSourceFile[] Markup) CreateOwnerGroups(int groupCount)
    {
        var sources = new List<SourceFile>();
        var markup = new List<AdditionalSourceFile>();
        for (var index = 0; index < groupCount; index++)
        {
            var child = $"Child{index}";
            sources.Add(TestComponentSources.CreateInteractiveComponent(
                componentName: $"CoveredParent{index}",
                renderTreeStatements: $$"""
                    __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                    __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                    {
                        __builder2.OpenComponent<global::TestComponents.{{child}}>(2);
                        __builder2.CloseComponent();
                    }));
                    __builder.AddAttribute(3, "ErrorContent", (global::Microsoft.AspNetCore.Components.RenderFragment<global::System.Exception>)(__error => (__builder3) =>
                    {
                        __builder3.OpenElement(4, "p");
                        __builder3.CloseElement();
                    }));
                    __builder.CloseComponent();
                    """));
            sources.Add(TestComponentSources.CreateInteractiveComponent(
                componentName: $"UncoveredParent{index}",
                renderTreeStatements: $$"""
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
                    __builder.CloseElement();
                    __builder.OpenComponent<global::TestComponents.{{child}}>(2);
                    __builder.CloseComponent();
                    """,
                razorMethods: "private void HandleClick() { }"));
            sources.Add(TestComponentSources.CreateInteractiveComponent(componentName: child, renderTreeStatements: ButtonRenderTree, razorMethods: "private void HandleClick() { }"));
            markup.Add(TestComponentSources.CreateRazorMarkup($"CoveredParent{index}", $"""
                @rendermode InteractiveServer
                <ErrorBoundary>
                    <ChildContent><{child} /></ChildContent>
                    <ErrorContent><p></p></ErrorContent>
                </ErrorBoundary>
                """));
            markup.Add(TestComponentSources.CreateRazorMarkup($"UncoveredParent{index}", $"""
                @rendermode InteractiveServer
                <button @onclick="HandleClick"></button>
                <{child} />
                """));
            markup.Add(TestComponentSources.CreateRazorMarkup(child, """
                @rendermode InteractiveServer
                <button @onclick="HandleClick"></button>
                """));
        }

        return (sources.ToArray(), markup.ToArray());
    }

    private static SourceFile CreateResultLoader(string componentName, string resultType) =>
        TestComponentSources.CreateInteractiveComponent(
            componentName: componentName,
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
                """);

    // Mirrors the source-following case: the lifecycle call goes through a project interface, and the implementation decides.
    private static SourceFile[] CreateFeatureGate(string componentName, string serviceName, string implementation) =>
    [
        TestComponentSources.CreateInteractiveComponent(
            componentName: componentName,
            renderTreeStatements: EmptyRenderTree,
            razorMethods: $$"""
                private global::TestComponents.I{{serviceName}} Features { get; } = new global::TestComponents.{{serviceName}}();

                protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                {
                    var enabled = await Features.IsEnabledAsync();
                }
                """),
        new SourceFile($"Services/{serviceName}.cs", $$"""
            namespace TestComponents;

            public interface I{{serviceName}}
            {
                global::System.Threading.Tasks.Task<bool> IsEnabledAsync();
            }

            public interface I{{serviceName}}Client
            {
                global::System.Threading.Tasks.Task<string> FetchAsync();
            }

            public sealed class {{serviceName}} : I{{serviceName}}
            {
                private I{{serviceName}}Client External { get; } = null!;
                private global::System.Net.Http.HttpClient Http { get; } = new();

                {{implementation}}
            }
            """),
    ];

    private static readonly SourceFile AppBoundary = new("Boundaries/AppBoundary.cs", """
        namespace TestComponents;

        public class AppBoundary : global::Microsoft.AspNetCore.Components.ErrorBoundaryBase
        {
            protected override global::System.Threading.Tasks.Task OnErrorAsync(global::System.Exception exception) => global::System.Threading.Tasks.Task.CompletedTask;
        }
        """);

    private static readonly SourceFile IntactCounterSource = TestComponentSources.CreateInteractiveComponent(
        componentName: "IntactCounter",
        renderTreeStatements: ButtonRenderTree,
        razorMethods: "private void HandleClick() { }");

    private static readonly AdditionalSourceFile IntactCounterMarkup = TestComponentSources.CreateRazorMarkup("IntactCounter", """
        @rendermode InteractiveServer
        <button @onclick="HandleClick">Click</button>
        """);

    private const string RenderTreeBuilderType = "global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder";

    private static MalformedCase GetMalformedCase(string name) => name switch
    {
        "UnclosedMethodBody" => new(
            [new SourceFile("Components/Broken.razor.g.cs", $$"""
                namespace TestComponents
                {
                    public partial class Broken : global::Microsoft.AspNetCore.Components.ComponentBase
                    {
                        protected override void BuildRenderTree({{RenderTreeBuilderType}} __builder)
                        {
                {{ButtonRenderTree}}
                        }

                        private void HandleClick()
                        {
                            var value = 1;
                """)],
            [],
            null, "CS1513"),
        "ExpressionBodiedBuildRenderTree" => new(
            [CreateRawComponent("Broken", $"protected override void BuildRenderTree({RenderTreeBuilderType} __builder) => __builder.AddAttribute(1, \"onclick\", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick)); private void HandleClick() {{ }}")],
            [],
            null, null),
        "BodylessBuildRenderTree" => new(
            [CreateRawComponent("Broken", $"protected override void BuildRenderTree({RenderTreeBuilderType} __builder);")],
            [],
            null, "CS0501"),
        "MissingBaseType" => new(
            [CreateRawComponent("Broken", $"protected override void BuildRenderTree({RenderTreeBuilderType} __builder) {{ {ButtonRenderTree} }} private void HandleClick() {{ }}", baseType: "global::Microsoft.AspNetCore.Components.ComponentBas")],
            [],
            null, "CS0234"),
        "CyclicBaseTypes" => new(
            [
                CreateRawComponent("Broken", $"protected override void BuildRenderTree({RenderTreeBuilderType} __builder) {{ {ButtonRenderTree} }} private void HandleClick() {{ }}", baseType: "global::TestComponents.Other"),
                new SourceFile("Components/Other.cs", "namespace TestComponents; public class Other : global::TestComponents.Broken { }"),
            ],
            [],
            null, "CS0146"),
        "FailedOverloadResolutionInHandler" => new(
            [TestComponentSources.CreateInteractiveComponent(
                componentName: "Broken",
                renderTreeStatements: ButtonRenderTree,
                razorMethods: "private void HandleClick() { new global::System.Net.Http.HttpClient().GetStringAsync(1, 2, 3, 4); }")],
            [],
            ("NTBA0001", "Broken"), "CS1501"),
        "FailedOverloadResolutionInEventCallback" => new(
            [TestComponentSources.CreateInteractiveComponent(
                componentName: "Broken",
                renderTreeStatements: """
                    __builder.OpenElement(0, "button");
                    __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick, 42));
                    __builder.CloseElement();
                    """,
                razorMethods: "private void HandleClick() { }")],
            [],
            null, "CS1501"),
        "DuplicateBuildRenderTreeOverride" => new(
            [TestComponentSources.CreateInteractiveComponent(
                componentName: "Broken",
                renderTreeStatements: ButtonRenderTree,
                razorMethods: $$"""
                    protected override void BuildRenderTree({{RenderTreeBuilderType}} __builder)
                    {
                    {{ButtonRenderTree}}
                    }

                    private void HandleClick() { }
                    """)],
            [],
            ("NTBA0001", "Broken"), "CS0111"),
        "RazorUnterminatedCodeBlock" => CreateRazorCase("""
            @rendermode InteractiveServer
            <button @onclick="HandleClick">Click</button>
            @code {
                private void HandleClick() {
            """),
        "RazorUnterminatedHtmlComment" => CreateRazorCase("""
            @rendermode InteractiveServer
            <button @onclick="HandleClick">Click</button>
            <!-- never closed
            <div></div>
            """),
        "RazorUnterminatedRazorComment" => CreateRazorCase("""
            @rendermode InteractiveServer
            <button @onclick="HandleClick">Click</button>
            @* never closed
            <div></div>
            """),
        "RazorUnterminatedQuotedAttribute" => CreateRazorCase("""
            @rendermode InteractiveServer
            <button @onclick="HandleClick>Click</button>
            """),
        "RazorStrayErrorBoundaryCloseTag" => CreateRazorCase("""
            @rendermode InteractiveServer
            </ErrorBoundary>
            <button @onclick="HandleClick">Click</button>
            </ErrorBoundary>
            """),
        "RazorTagEndingInDot" => CreateRazorCase("""
            @rendermode InteractiveServer
            <Foo.>
            <button @onclick="HandleClick">Click</button>
            </Foo.>
            """),
        "RazorTagThatIsOnlyADot" => CreateRazorCase("""
            @rendermode InteractiveServer
            <.>
            <button @onclick="HandleClick">Click</button>
            </.>
            """),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown malformed case."),
    };

    // The generated code is intact and uncovered, so NTBA0001 for the component must survive whatever the markup parser makes of the text.
    private static MalformedCase CreateRazorCase(string markup) =>
        new(
            [TestComponentSources.CreateInteractiveComponent(componentName: "Broken", renderTreeStatements: ButtonRenderTree, razorMethods: "private void HandleClick() { }")],
            [TestComponentSources.CreateRazorMarkup("Broken", markup)],
            ("NTBA0001", "Broken"),
            null);

    private static SourceFile CreateRawComponent(string componentName, string members, string baseType = "global::Microsoft.AspNetCore.Components.ComponentBase") =>
        new($"Components/{componentName}.razor.g.cs", $$"""
            namespace TestComponents
            {
                [global::TestComponents.{{componentName}}.InteractiveAttribute]
                public partial class {{componentName}} : {{baseType}}
                {
                    {{members}}

                    private sealed class InteractiveAttribute : global::Microsoft.AspNetCore.Components.RenderModeAttribute
                    {
                        public override global::Microsoft.AspNetCore.Components.IComponentRenderMode Mode => global::Microsoft.AspNetCore.Components.Web.RenderMode.InteractiveServer;
                    }
                }
            }
            """);

    private sealed record Fixture(SourceFile[] Sources, AdditionalSourceFile[] AdditionalFiles, string[] ExpectedIds);

    private sealed record MalformedCase(SourceFile[] Sources, AdditionalSourceFile[] AdditionalFiles, (string Id, string Component)? ExpectedComponentDiagnostic, string? CompilerError);
}
