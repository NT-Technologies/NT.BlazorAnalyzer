using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace NT.BlazorAnalyzer.Tests;

public sealed class RazorMarkupAnalyzerWhiteBoxTests
{
    [Fact]
    public void SkipHelpers_HandleFoundAndMissingTerminators()
    {
        Assert.Equal(4, (int)AnalyzerWhiteBoxTestHarness.InvokeRazorAnalyzer("SkipUntil", "ab*@c", 0, "*@")!);
        Assert.Equal(4, (int)AnalyzerWhiteBoxTestHarness.InvokeRazorAnalyzer("SkipUntil", "text", 0, "-->")!);
        Assert.True((bool)AnalyzerWhiteBoxTestHarness.InvokeRazorAnalyzer("StartsWithDirectiveBlock", "@code { }", 0, "code", -1)!);
        Assert.False((bool)AnalyzerWhiteBoxTestHarness.InvokeRazorAnalyzer("StartsWithDirectiveBlock", "@code x", 0, "code", -1)!);
        Assert.Equal(5, (int)AnalyzerWhiteBoxTestHarness.InvokeRazorAnalyzer("SkipQuotedString", "\"a\\\"b\"", 0)!);
        Assert.Equal(3, (int)AnalyzerWhiteBoxTestHarness.InvokeRazorAnalyzer("SkipQuotedString", "\"abc", 0)!);
        Assert.Equal(3, (int)AnalyzerWhiteBoxTestHarness.InvokeRazorAnalyzer("SkipLineComment", "//x\nz", 2)!);
        Assert.Equal(5, (int)AnalyzerWhiteBoxTestHarness.InvokeRazorAnalyzer("SkipLineComment", "//xyz", 2)!);
        Assert.Equal(4, (int)AnalyzerWhiteBoxTestHarness.InvokeRazorAnalyzer("SkipBlockComment", "/*x*/z", 2)!);
        Assert.Equal(3, (int)AnalyzerWhiteBoxTestHarness.InvokeRazorAnalyzer("SkipBlockComment", "/*xx", 2)!);
    }

    [Fact]
    public void TryParseTag_AndParseAttributes_HandleEdgeCases()
    {
        var parseTagMethod = typeof(RazorMarkupAnalyzer).GetMethod("TryParseTag", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var parameters = new object?[] { "<Button Visible Title=@($\"X {1}\") disabled />", 0, null };
        var parsed = (bool)parseTagMethod.Invoke(null, parameters)!;
        Assert.True(parsed);

        var tag = parameters[2]!;
        Assert.Equal("Button", AnalyzerWhiteBoxTestHarness.GetProperty<string>(tag, "Name"));
        Assert.True(AnalyzerWhiteBoxTestHarness.GetProperty<bool>(tag, "IsSelfClosing"));
        var attributes = tag.GetType().GetProperty("Attributes")!.GetValue(tag)!;
        Assert.Equal(3, (int)attributes.GetType().GetProperty("Length")!.GetValue(attributes)!);

        var invalidParameters = new object?[] { "< >", 0, null };
        Assert.False((bool)parseTagMethod.Invoke(null, invalidParameters)!);

        var parsedAttributes = typeof(RazorMarkupAnalyzer)
            .GetMethod("ParseAttributes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [" disabled value=@(1 + 2) data-value=test ", 0, 39])!;
        Assert.Equal(3, (int)parsedAttributes!.GetType().GetProperty("Length")!.GetValue(parsedAttributes)!);
    }

    [Fact]
    public void HtmlLocationHelpers_PreferEventThenBind_AndSupportNullAttributes()
    {
        var markup = "<input @onclick=\"Save\" @bind-Value=\"CurrentValue\" />";
        var sourceText = SourceText.From(markup);

        var parseTagMethod = typeof(RazorMarkupAnalyzer).GetMethod("TryParseTag", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var parseParameters = new object?[] { markup, 0, null };
        Assert.True((bool)parseTagMethod.Invoke(null, parseParameters)!);
        var tag = parseParameters[2]!;
        var attributes = tag.GetType().GetProperty("Attributes")!.GetValue(tag)!;

        var tryGetHtmlInteractiveLocation = typeof(RazorMarkupAnalyzer)
            .GetMethod("TryGetHtmlInteractiveLocation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var args = new object?[] { attributes, sourceText, "Page.razor", null };
        Assert.True((bool)tryGetHtmlInteractiveLocation.Invoke(null, args)!);
        var location = (Location)args[3]!;
        Assert.Equal(markup.IndexOf("@onclick", StringComparison.Ordinal), location.SourceSpan.Start);

        var noAttributes = ImmutableArray<RazorMarkupRegion>.Empty;
        var createLocation = typeof(RazorMarkupAnalyzer)
            .GetMethod("TryCreateAttributeLocation", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Null(createLocation.Invoke(null, [sourceText, "Page.razor", null]));

        var bindOnlyMarkup = "<input @bind-Value=\"CurrentValue\" />";
        var bindParseParameters = new object?[] { bindOnlyMarkup, 0, null };
        Assert.True((bool)parseTagMethod.Invoke(null, bindParseParameters)!);
        var bindTag = bindParseParameters[2]!;
        var bindAttributes = bindTag.GetType().GetProperty("Attributes")!.GetValue(bindTag)!;
        var bindArgs = new object?[] { bindAttributes, SourceText.From(bindOnlyMarkup), "Page.razor", null };
        Assert.True((bool)tryGetHtmlInteractiveLocation.Invoke(null, bindArgs)!);
        Assert.NotNull(bindArgs[3]);

        var emptyAttributes = AnalyzerWhiteBoxTestHarness.GetRazorAnalyzerMethod("ParseAttributes", 3)
            .Invoke(null, ["", 0, 0])!;
        var emptyArgs = new object?[] { emptyAttributes, SourceText.From("<div></div>"), "Page.razor", null };
        Assert.False((bool)tryGetHtmlInteractiveLocation.Invoke(null, emptyArgs)!);

    }

    [Fact]
    public void Analyze_AndParserHelpers_CoverInvalidTags_UnmatchedClosures_AndDanglingAttributes()
    {
        var analysis = RazorMarkupAnalyzer.Analyze(
            SourceText.From("<?xml version=\"1.0\"?>< > </Missing><button @onclick=\"Save\"></button>"),
            "Page.razor",
            ImmutableHashSet<string>.Empty);
        Assert.Single(analysis.HtmlInteractiveRegions);

        var parseAttributes = AnalyzerWhiteBoxTestHarness.GetRazorAnalyzerMethod("ParseAttributes", 3);
        var dangling = parseAttributes.Invoke(null, [" = value @bind-Value= ", 0, 21])!;
        Assert.Equal(2, (int)dangling.GetType().GetProperty("Length")!.GetValue(dangling)!);

        var closeTag = AnalyzerWhiteBoxTestHarness.GetRazorAnalyzerMethod("CloseTag", 2);
        var stackType = typeof(Stack<>).MakeGenericType(typeof(RazorMarkupAnalyzer).GetNestedType("TagFrame", System.Reflection.BindingFlags.NonPublic)!);
        var stack = Activator.CreateInstance(stackType)!;
        var tagFrameType = typeof(RazorMarkupAnalyzer).GetNestedType("TagFrame", System.Reflection.BindingFlags.NonPublic)!;
        var ctor = tagFrameType.GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public).Single();
        stackType.GetMethod("Push")!.Invoke(stack, [ctor.Invoke(["Outer", false, false])]);
        closeTag.Invoke(null, [stack, "Missing"]);
        Assert.Equal(0, (int)stackType.GetProperty("Count")!.GetValue(stack)!);
    }
}
