using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class RazorMarkupAnalyzerTests
{
    [Fact]
    public void Analyze_WithMissingSource_ReturnsNull()
    {
        var analysis = RazorMarkupAnalyzer.Analyze(
            "Components/Test.razor",
            _ => null,
            ImmutableHashSet<string>.Empty);

        Assert.Null(analysis);
    }

    [Fact]
    public void Analyze_IgnoresCommentsAndDirectiveBlocks_AndCapturesHtmlInteractiveRegions()
    {
        var markup = """
            @* razor comment with <button @onclick="Ignored"></button> *@
            <!-- html comment with <input @bind-Value="Ignored" /> -->
            @code {
                // line comment
                /* block comment */
                var text = "<div></div>";
                if (text == "{")
                {
                    text = "}";
                }
            }
            <button @onclick="HandleClick">Click</button>
            <input @bind-Value="CurrentValue" disabled />
            """;

        var analysis = RazorMarkupAnalyzer.Analyze(
            SourceText.From(markup),
            "Components/Test.razor",
            ImmutableHashSet<string>.Empty);

        Assert.False(analysis.HasBoundaryRoot);
        Assert.Equal(2, analysis.HtmlInteractiveRegions.Length);
        Assert.All(analysis.HtmlInteractiveRegions, region => Assert.True(region.DiagnosticLocation.SourceSpan.Length >= 0));
        Assert.Equal("button", analysis.HtmlInteractiveRegions[0].TagName);
        Assert.Equal("input", analysis.HtmlInteractiveRegions[1].TagName);
    }

    [Fact]
    public void Analyze_CapturesComponentRoots_AndBindLocation_ForNamespacedComponents()
    {
        var markup = """
            <TestComponents.EditorForm @bind-Value="CurrentValue" />
            <EditorToolbar Changed="HandleChanged" Title=@($"Edit {CurrentValue}") Visible />
            """;

        var analysis = RazorMarkupAnalyzer.Analyze(
            SourceText.From(markup),
            "Components/EditPage.razor",
            ImmutableHashSet<string>.Empty);

        Assert.Equal(2, analysis.ComponentRoots.Length);
        Assert.Equal("TestComponents.EditorForm", analysis.ComponentRoots[0].TagName);
        Assert.NotNull(analysis.ComponentRoots[0].BindingLocation);
        Assert.Equal("EditorToolbar", analysis.ComponentRoots[1].TagName);
        Assert.NotNull(analysis.ComponentRoots[1].RootLocation);
    }

    [Fact]
    public void Analyze_RecognizesBoundaryRoots_AndErrorContent_WithKey()
    {
        var markup = """
            <TestComponents.CustomBoundary @key="CurrentRoute">
                <button @onclick="HandleClick">Click</button>
                <ErrorContent>
                    <p>Failed</p>
                </ErrorContent>
            </TestComponents.CustomBoundary>
            """;

        var analysis = RazorMarkupAnalyzer.Analyze(
            SourceText.From(markup),
            "Components/ProtectedPage.razor",
            ImmutableHashSet.Create("CustomBoundary"));

        Assert.True(analysis.HasBoundaryRoot);
        Assert.True(analysis.BoundaryRootHasErrorContent);
        Assert.True(analysis.BoundaryRootIsKeyed);
        Assert.Empty(analysis.HtmlInteractiveRegions);
        Assert.NotNull(analysis.BoundaryRootLocation);
    }

    [Fact]
    public void Analyze_LeavesBoundaryRootMissingErrorContent_WhenInnerBoundaryExistsWithoutFallback()
    {
        var markup = """
            <ErrorBoundary>
                <ChildContent>
                    <ErrorBoundary />
                </ChildContent>
            </ErrorBoundary>
            """;

        var analysis = RazorMarkupAnalyzer.Analyze(
            SourceText.From(markup),
            "Components/BoundaryPage.razor",
            ImmutableHashSet<string>.Empty);

        Assert.True(analysis.HasBoundaryRoot);
        Assert.False(analysis.BoundaryRootHasErrorContent);
    }

    [Fact]
    public void Analyze_Ignores_PageTitle_And_HeadContent_ComponentRoots()
    {
        var markup = """
            <PageTitle>Title</PageTitle>
            <HeadContent>
                <meta />
            </HeadContent>
            <SectionHeader />
            """;

        var analysis = RazorMarkupAnalyzer.Analyze(
            SourceText.From(markup),
            "Components/HeaderPage.razor",
            ImmutableHashSet<string>.Empty);

        Assert.Single(analysis.ComponentRoots);
        Assert.Equal("SectionHeader", analysis.ComponentRoots[0].TagName);
    }
}
