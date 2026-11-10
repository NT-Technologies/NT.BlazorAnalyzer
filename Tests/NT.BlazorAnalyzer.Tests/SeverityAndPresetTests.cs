using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

/// <summary>
/// Behavior source: docs/rules/README.md rules table ("Default severity") and "Severity mode" section.
/// </summary>
public sealed partial class SeverityAndPresetTests
{
    private const string EmptyRenderTree = """
        __builder.OpenElement(0, "div");
        __builder.CloseElement();
        """;

    private const string ClickRenderTree = """
        __builder.OpenElement(0, "button");
        __builder.AddAttribute(1, "onclick", global::Microsoft.AspNetCore.Components.EventCallback.Factory.Create<global::Microsoft.AspNetCore.Components.Web.MouseEventArgs>(this, HandleClick));
        __builder.CloseElement();
        """;

    private const string BackgroundMembers = """
        private DataService Service { get; } = new();

        public sealed class DataService
        {
            public global::System.Threading.Tasks.Task LoadAsync() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data");

            public void Load() => new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test/data").Wait();

            public event global::System.EventHandler? Changed;
        }
        """;

    [Theory]
    [InlineData("NTBA0001", DiagnosticSeverity.Info)]
    [InlineData("NTBA0002", DiagnosticSeverity.Info)]
    [InlineData("NTBA0003", DiagnosticSeverity.Warning)]
    [InlineData("NTBA0004", DiagnosticSeverity.Warning)]
    [InlineData("NTBA0005", DiagnosticSeverity.Warning)]
    [InlineData("NTBA0006", DiagnosticSeverity.Warning)]
    [InlineData("NTBA0007", DiagnosticSeverity.Warning)]
    [InlineData("NTBA0008", DiagnosticSeverity.Warning)]
    [InlineData("NTBA0009", DiagnosticSeverity.Info)]
    [InlineData("NTBA0010", DiagnosticSeverity.Warning)]
    [InlineData("NTBA0011", DiagnosticSeverity.Warning)]
    [InlineData("NTBA0012", DiagnosticSeverity.Warning)]
    public async Task Rule_HasDocumentedDefaultSeverity(string ruleId, DiagnosticSeverity expectedSeverity)
    {
        var (sources, additionalFiles) = CreateTrigger(ruleId);

        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(sources, additionalFiles);

        // Reporting at all proves the trigger is live, so the severity assertion cannot pass vacuously.
        var reported = diagnostics.Where(diagnostic => diagnostic.Id == ruleId).ToArray();
        Assert.NotEmpty(reported);
        Assert.All(reported, diagnostic => Assert.Equal(expectedSeverity, diagnostic.Severity));
    }

    [Fact]
    public void StrictPreset_RaisesBoundaryRulesToWarning_AndEnablesUnknownAwaits_AtLowPriority()
    {
        var entries = ReadPresetEntries();

        Assert.Equal("true", entries["is_global"]);
        // A negative level lets the consumer's own .globalconfig and .editorconfig win over the preset.
        Assert.Equal("-1", entries["global_level"]);
        Assert.Equal("warning", entries["dotnet_diagnostic.NTBA0001.severity"]);
        Assert.Equal("warning", entries["dotnet_diagnostic.NTBA0002.severity"]);
        Assert.Equal("true", entries["ntba.unknown_awaits_are_failure_prone"]);
    }

    [Fact]
    public void StrictPreset_ChangesOnlyTheDocumentedRules()
    {
        var severityKeys = ReadPresetEntries().Keys.Where(static key => key.StartsWith("dotnet_diagnostic.", StringComparison.Ordinal)).Order().ToArray();

        // "Other rules keep their severities from the table above."
        Assert.Equal(["dotnet_diagnostic.NTBA0001.severity", "dotnet_diagnostic.NTBA0002.severity"], severityKeys);
    }

    [Fact]
    public void Targets_IncludeStrictPreset_OnlyWhenModeIsStrict()
    {
        var targets = XDocument.Load(Path.Combine(FindBuildDirectory(), "NT.BlazorAnalyzer.targets"));

        var includes = targets.Descendants()
            .Where(static element => element.Attribute("Include")?.Value.Contains("NT.BlazorAnalyzer.strict.globalconfig", StringComparison.Ordinal) == true)
            .ToArray();

        var include = Assert.Single(includes);
        Assert.Equal("EditorConfigFiles", include.Name.LocalName);
        var condition = Assert.IsType<string>(include.Parent?.Attribute("Condition")?.Value);
        Assert.Matches(StrictCondition(), condition);
    }

    [Fact]
    public void StrictPreset_ReferencedByTargets_ExistsNextToThem()
    {
        Assert.True(File.Exists(Path.Combine(FindBuildDirectory(), "NT.BlazorAnalyzer.strict.globalconfig")));
    }

    // The preset's ntba.* entries are fed through the harness as global options, which is how an .editorconfig-based
    // consumer receives them. dotnet_diagnostic.*.severity entries cannot be exercised here: severity remapping is
    // applied by the compiler's SyntaxTreeOptionsProvider, which the harness's AnalyzerOptions-only setup does not model.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task StrictPresetSettings_CountUnknownAwaitedCallsAsFailureProne(bool applyPreset, bool expectsNtba0003)
    {
        var presetSettings = ReadPresetEntries()
            .Where(static entry => entry.Key.StartsWith("ntba.", StringComparison.Ordinal))
            .ToDictionary(static entry => entry.Key, static entry => entry.Value);

        var diagnostics = await AnalyzerTestHarness.GetDiagnosticsAsync(
            [
                TestComponentSources.CreateInteractiveComponent(
                    componentName: "Probe",
                    renderTreeStatements: EmptyRenderTree,
                    razorMethods: """
                        // No implementation in the compilation and no operational naming: no evidence of failure.
                        private IHelper Helper { get; } = null!;

                        protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                        {
                            await Helper.RunAsync();
                        }

                        public interface IHelper
                        {
                            global::System.Threading.Tasks.Task RunAsync();
                        }
                        """)
            ],
            globalOptions: applyPreset ? presetSettings : null);

        Assert.Equal(expectsNtba0003, diagnostics.Any(static diagnostic => diagnostic.Id == "NTBA0003"));
    }

    private static (SourceFile[] Sources, AdditionalSourceFile[]? AdditionalFiles) CreateTrigger(string ruleId)
    {
        SourceFile[] Interactive(string renderTree, string? methods) =>
            [TestComponentSources.CreateInteractiveComponent("Probe", renderTree, methods)];

        return ruleId switch
        {
            "NTBA0001" or "NTBA0002" => (Interactive(ClickRenderTree, "private void HandleClick() => throw new global::System.InvalidOperationException();"), null),
            "NTBA0003" => (Interactive(EmptyRenderTree, """
                protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
                {
                    await new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test");
                }
                """), null),
            "NTBA0004" => (Interactive(EmptyRenderTree, """
                public void Dispose()
                {
                    new global::System.Net.Http.HttpClient().GetStringAsync("https://example.test").Wait();
                }
                """), null),
            "NTBA0005" => (Interactive(ClickRenderTree, """
                private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                private async global::System.Threading.Tasks.Task HandleClick()
                {
                    await JS.InvokeVoidAsync("doSomething");
                }
                """), null),
            "NTBA0006" => (Interactive(EmptyRenderTree, """
                private global::Microsoft.JSInterop.IJSRuntime JS => default!;

                protected override async global::System.Threading.Tasks.Task OnInitializedAsync()
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
                """), null),
            "NTBA0007" => (Interactive(ClickRenderTree, """
                private async void HandleClick()
                {
                    await global::System.Threading.Tasks.Task.CompletedTask;
                }
                """), null),
            "NTBA0008" => (Interactive(ClickRenderTree, """
                private void HandleClick()
                {
                    try
                    {
                        throw new global::System.InvalidOperationException();
                    }
                    catch (global::System.Exception)
                    {
                    }
                }
                """), null),
            "NTBA0009" => (Interactive("""
                __builder.OpenComponent<global::Microsoft.AspNetCore.Components.Web.ErrorBoundary>(0);
                __builder.AddAttribute(1, "ChildContent", (global::Microsoft.AspNetCore.Components.RenderFragment)((__builder2) =>
                {
                    __builder2.OpenElement(2, "div");
                    __builder2.CloseElement();
                }));
                __builder.CloseComponent();
                """, null), null),
            "NTBA0010" => (
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
                ]),
            "NTBA0011" => (Interactive(EmptyRenderTree, BackgroundMembers + """

                private void Start() { _ = Service.LoadAsync(); }
                """), null),
            "NTBA0012" => (Interactive(EmptyRenderTree, BackgroundMembers + """

                private void Start() => Service.Changed += (_, _) => Service.Load();
                """), null),
            _ => throw new ArgumentOutOfRangeException(nameof(ruleId), ruleId, null)
        };
    }

    private static Dictionary<string, string> ReadPresetEntries()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadAllLines(Path.Combine(FindBuildDirectory(), "NT.BlazorAnalyzer.strict.globalconfig")))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] is '#' or ';')
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            Assert.True(separator > 0, $"Unparseable preset line: {trimmed}");
            entries[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }

        return entries;
    }

    // The source file path is a second starting point because the build output may live outside the repository (artifacts output).
    private static string FindBuildDirectory([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "")
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(sourcePath) })
        {
            for (var directory = start is null ? null : new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "NT.BlazorAnalyzer.slnx")))
                {
                    return Path.Combine(directory.FullName, "NT.BlazorAnalyzer", "build");
                }
            }
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    [GeneratedRegex(@"^\s*'\$\(NTBlazorAnalyzerMode\)'\s*==\s*'strict'\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex StrictCondition();
}
