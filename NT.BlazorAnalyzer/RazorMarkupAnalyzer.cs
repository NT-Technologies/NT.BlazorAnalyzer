using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace NT.BlazorAnalyzer;

internal static class RazorMarkupAnalyzer
{
    public static RazorMarkupAnalysis? Analyze(
        string razorPath,
        Func<string, SourceText?> tryGetSourceText,
        ImmutableHashSet<string> boundaryComponentNames)
    {
        var sourceText = tryGetSourceText(razorPath);
        return sourceText is null ? null : Analyze(sourceText, razorPath, boundaryComponentNames);
    }

    public static RazorMarkupAnalysis Analyze(
        SourceText sourceText,
        string razorPath,
        ImmutableHashSet<string> boundaryComponentNames)
    {
        var text = sourceText.ToString();
        var stack = new Stack<TagFrame>();
        var hasBoundaryProtectedContent = false;
        var rootBoundaryHasErrorContent = true;
        var rootBoundaryIsKeyed = false;
        Location? boundaryRootLocation = null;
        var htmlInteractiveRegions = ImmutableArray.CreateBuilder<RazorMarkupRegion>();
        var componentRoots = ImmutableArray.CreateBuilder<RazorComponentRoot>();

        for (var index = 0; index < text.Length;)
        {
            if (StartsWith(text, index, "@*"))
            {
                index = SkipUntil(text, index + 2, "*@");
                continue;
            }

            if (StartsWith(text, index, "<!--"))
            {
                index = SkipUntil(text, index + 4, "-->");
                continue;
            }

            if (StartsWithDirectiveBlock(text, index, "code", out var codeBlockStart) ||
                StartsWithDirectiveBlock(text, index, "functions", out codeBlockStart))
            {
                index = SkipBalancedBlock(text, codeBlockStart);
                continue;
            }

            if (text[index] != '<' || index + 1 >= text.Length)
            {
                index++;
                continue;
            }

            if (text[index + 1] is '!' or '?')
            {
                index++;
                continue;
            }

            if (!TryParseTag(text, index, out var tag))
            {
                index++;
                continue;
            }

            index = tag.EndIndex;

            if (tag.IsClosingTag)
            {
                CloseTag(stack, tag.Name);
                continue;
            }

            var isIgnoredRoot = IsIgnoredRootComponent(tag.Name);
            var isBoundary = IsBoundaryTag(tag.Name, boundaryComponentNames);
            var isComponent = IsComponentTag(tag.Name);
            var activeBoundaryCount = stack.Count(frame => frame.IsBoundary);

            if (stack.Count == 0 && isBoundary)
            {
                boundaryRootLocation ??= CreateLocation(sourceText, razorPath, tag.NameSpan);
                hasBoundaryProtectedContent = true;
                rootBoundaryHasErrorContent = false;
                rootBoundaryIsKeyed = HasKeyDirective(tag.Attributes);
            }

            if (stack.Count == 1 &&
                stack.Peek().IsBoundary &&
                string.Equals(GetSimpleTagName(tag.Name), "ErrorContent", StringComparison.Ordinal))
            {
                rootBoundaryHasErrorContent = true;
            }

            if (activeBoundaryCount == 0 && !isIgnoredRoot && !isBoundary)
            {
                if (isComponent)
                {
                    componentRoots.Add(new RazorComponentRoot(
                        tag.Name,
                        CreateLocation(sourceText, razorPath, tag.NameSpan),
                        TryCreateAttributeLocation(sourceText, razorPath, GetBindAttribute(tag.Attributes))));
                }
                else if (TryGetHtmlInteractiveLocation(tag.Attributes, sourceText, razorPath, out var htmlInteractiveLocation))
                {
                    htmlInteractiveRegions.Add(new RazorMarkupRegion(
                        InteractiveRenderRegionKind.HtmlEventHandler,
                        tag.Name,
                        htmlInteractiveLocation));
                }
            }

            if (activeBoundaryCount > 0 && isBoundary)
            {
                hasBoundaryProtectedContent = true;
            }

            if (!tag.IsSelfClosing)
            {
                stack.Push(new TagFrame(tag.Name, isBoundary, isIgnoredRoot));
            }
        }

        return new RazorMarkupAnalysis(
            hasBoundaryRoot: hasBoundaryProtectedContent && htmlInteractiveRegions.Count == 0,
            boundaryRootHasErrorContent: rootBoundaryHasErrorContent,
            boundaryRootIsKeyed: rootBoundaryIsKeyed,
            boundaryRootLocation,
            htmlInteractiveRegions: htmlInteractiveRegions.ToImmutable(),
            componentRoots: componentRoots.ToImmutable());
    }

    private static bool StartsWith(string text, int index, string value) =>
        index >= 0 &&
        index + value.Length <= text.Length &&
        string.Compare(text, index, value, 0, value.Length, StringComparison.Ordinal) == 0;

    private static int SkipUntil(string text, int index, string terminator)
    {
        var terminatorIndex = text.IndexOf(terminator, index, StringComparison.Ordinal);
        return terminatorIndex >= 0 ? terminatorIndex + terminator.Length : text.Length;
    }

    private static bool StartsWithDirectiveBlock(string text, int index, string directiveName, out int blockStartIndex)
    {
        blockStartIndex = -1;
        if (!StartsWith(text, index, "@" + directiveName))
        {
            return false;
        }

        var cursor = index + directiveName.Length + 1;
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor]))
        {
            cursor++;
        }

        if (cursor >= text.Length || text[cursor] != '{')
        {
            return false;
        }

        blockStartIndex = cursor;
        return true;
    }

    private static int SkipBalancedBlock(string text, int blockStartIndex)
    {
        var depth = 0;
        for (var index = blockStartIndex; index < text.Length; index++)
        {
            var current = text[index];
            if (current == '{')
            {
                depth++;
                continue;
            }

            if (current == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }

                continue;
            }

            if (current is '"' or '\'')
            {
                index = SkipQuotedString(text, index);
                continue;
            }

            if (current == '/' && index + 1 < text.Length)
            {
                if (text[index + 1] == '/')
                {
                    index = SkipLineComment(text, index + 2);
                }
                else if (text[index + 1] == '*')
                {
                    index = SkipBlockComment(text, index + 2);
                }
            }
        }

        return text.Length;
    }

    private static int SkipQuotedString(string text, int quoteIndex)
    {
        var quote = text[quoteIndex];
        for (var index = quoteIndex + 1; index < text.Length; index++)
        {
            if (text[index] == '\\')
            {
                index++;
                continue;
            }

            if (text[index] == quote)
            {
                return index;
            }
        }

        return text.Length - 1;
    }

    private static int SkipLineComment(string text, int index)
    {
        while (index < text.Length && text[index] != '\n')
        {
            index++;
        }

        return index;
    }

    private static int SkipBlockComment(string text, int index)
    {
        var commentEnd = text.IndexOf("*/", index, StringComparison.Ordinal);
        return commentEnd >= 0 ? commentEnd + 1 : text.Length - 1;
    }

    private static bool TryParseTag(string text, int startIndex, out ParsedTag tag)
    {
        tag = default;
        var index = startIndex + 1;
        var isClosingTag = false;
        if (index < text.Length && text[index] == '/')
        {
            isClosingTag = true;
            index++;
        }

        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        var nameStart = index;
        while (index < text.Length && IsTagNameCharacter(text[index]))
        {
            index++;
        }

        if (index == nameStart)
        {
            return false;
        }

        var name = text.Substring(nameStart, index - nameStart);
        var attributesStart = index;
        var selfClosing = false;
        var inQuote = false;
        var quote = '\0';

        while (index < text.Length)
        {
            var current = text[index];
            if (inQuote)
            {
                if (current == quote)
                {
                    inQuote = false;
                }

                index++;
                continue;
            }

            if (current is '"' or '\'')
            {
                inQuote = true;
                quote = current;
                index++;
                continue;
            }

            if (current == '>')
            {
                if (index > startIndex && text[index - 1] == '/')
                {
                    selfClosing = true;
                }

                index++;
                break;
            }

            index++;
        }

        var attributesEnd = Math.Max(attributesStart, index - 1);
        tag = new ParsedTag(
            name,
            new TextSpan(nameStart, name.Length),
            ParseAttributes(text, attributesStart, attributesEnd),
            isClosingTag,
            selfClosing,
            index);
        return true;
    }

    private static ImmutableArray<ParsedAttribute> ParseAttributes(string text, int startIndex, int endIndex)
    {
        if (endIndex <= startIndex)
        {
            return [];
        }

        var attributes = ImmutableArray.CreateBuilder<ParsedAttribute>();
        for (var index = startIndex; index < endIndex;)
        {
            while (index < endIndex && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= endIndex || text[index] is '/' or '>')
            {
                break;
            }

            var attributeStart = index;
            while (index < endIndex &&
                   !char.IsWhiteSpace(text[index]) &&
                   text[index] is not '=' and not '/' and not '>')
            {
                index++;
            }

            if (index == attributeStart)
            {
                index++;
                continue;
            }

            var name = text.Substring(attributeStart, index - attributeStart);
            attributes.Add(new ParsedAttribute(name, new TextSpan(attributeStart, index - attributeStart)));

            while (index < endIndex && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= endIndex || text[index] != '=')
            {
                continue;
            }

            index++;
            while (index < endIndex && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            if (index >= endIndex)
            {
                break;
            }

            if (text[index] is '"' or '\'')
            {
                var quote = text[index++];
                while (index < endIndex)
                {
                    if (text[index] == quote)
                    {
                        index++;
                        break;
                    }

                    index++;
                }

                continue;
            }

            var depth = 0;
            while (index < endIndex)
            {
                var current = text[index];
                if (current == '(')
                {
                    depth++;
                }
                else if (current == ')' && depth > 0)
                {
                    depth--;
                }
                else if (depth == 0 && (char.IsWhiteSpace(current) || current is '/' or '>'))
                {
                    break;
                }

                index++;
            }
        }

        return attributes.ToImmutable();
    }

    private static bool IsTagNameCharacter(char value) =>
        char.IsLetterOrDigit(value) ||
        value is '_' or ':' or '.' or '-';

    private static void CloseTag(Stack<TagFrame> stack, string tagName)
    {
        while (stack.Count > 0)
        {
            var frame = stack.Pop();
            if (string.Equals(frame.Name, tagName, StringComparison.Ordinal))
            {
                return;
            }
        }
    }

    private static bool TryGetHtmlInteractiveLocation(
        ImmutableArray<ParsedAttribute> attributes,
        SourceText sourceText,
        string razorPath,
        out Location diagnosticLocation)
    {
        if (GetEventAttribute(attributes) is { } eventAttribute)
        {
            diagnosticLocation = CreateLocation(sourceText, razorPath, eventAttribute.NameSpan);
            return true;
        }

        if (GetBindAttribute(attributes) is { } bindAttribute)
        {
            diagnosticLocation = CreateLocation(sourceText, razorPath, bindAttribute.NameSpan);
            return true;
        }

        diagnosticLocation = null!;
        return false;
    }

    private static ParsedAttribute? GetEventAttribute(ImmutableArray<ParsedAttribute> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Name.StartsWith("@on", StringComparison.OrdinalIgnoreCase))
            {
                return attribute;
            }
        }

        return null;
    }

    private static ParsedAttribute? GetBindAttribute(ImmutableArray<ParsedAttribute> attributes)
    {
        foreach (var attribute in attributes)
        {
            if (attribute.Name.StartsWith("@bind-", StringComparison.OrdinalIgnoreCase))
            {
                return attribute;
            }
        }

        return null;
    }

    private static bool HasKeyDirective(ImmutableArray<ParsedAttribute> attributes) =>
        attributes.Any(static attribute => attribute.Name.Equals("@key", StringComparison.OrdinalIgnoreCase));

    private static bool IsBoundaryTag(string tagName, ImmutableHashSet<string> boundaryComponentNames)
    {
        var simpleTagName = GetSimpleTagName(tagName);
        return string.Equals(simpleTagName, "ErrorBoundary", StringComparison.Ordinal) ||
            boundaryComponentNames.Contains(simpleTagName);
    }

    private static bool IsComponentTag(string tagName) =>
        !string.IsNullOrEmpty(tagName) &&
        (char.IsUpper(GetSimpleTagName(tagName)[0]) || tagName.IndexOf('.') >= 0);

    private static bool IsIgnoredRootComponent(string tagName)
    {
        var simpleTagName = GetSimpleTagName(tagName);
        return simpleTagName is "PageTitle" or "HeadContent";
    }

    private static string GetSimpleTagName(string tagName)
    {
        var lastSeparator = tagName.LastIndexOf('.');
        return lastSeparator >= 0 ? tagName.Substring(lastSeparator + 1) : tagName;
    }

    private static Location CreateLocation(SourceText sourceText, string path, TextSpan span) =>
        Location.Create(path, span, sourceText.Lines.GetLinePositionSpan(span));

    private static Location? TryCreateAttributeLocation(SourceText sourceText, string razorPath, ParsedAttribute? attribute) =>
        attribute is null ? null : CreateLocation(sourceText, razorPath, attribute.Value.NameSpan);

    private readonly struct ParsedTag
    {
        public ParsedTag(string name, TextSpan nameSpan, ImmutableArray<ParsedAttribute> attributes, bool isClosingTag, bool isSelfClosing, int endIndex)
        {
            Name = name;
            NameSpan = nameSpan;
            Attributes = attributes;
            IsClosingTag = isClosingTag;
            IsSelfClosing = isSelfClosing;
            EndIndex = endIndex;
        }

        public string Name { get; }

        public TextSpan NameSpan { get; }

        public ImmutableArray<ParsedAttribute> Attributes { get; }

        public bool IsClosingTag { get; }

        public bool IsSelfClosing { get; }

        public int EndIndex { get; }
    }

    private readonly struct ParsedAttribute
    {
        public ParsedAttribute(string name, TextSpan nameSpan)
        {
            Name = name;
            NameSpan = nameSpan;
        }

        public string Name { get; }

        public TextSpan NameSpan { get; }
    }

    private readonly struct TagFrame
    {
        public TagFrame(string name, bool isBoundary, bool isIgnoredRoot)
        {
            Name = name;
            IsBoundary = isBoundary;
            IsIgnoredRoot = isIgnoredRoot;
        }

        public string Name { get; }

        public bool IsBoundary { get; }

        public bool IsIgnoredRoot { get; }
    }
}

internal sealed class RazorMarkupAnalysis
{
    public RazorMarkupAnalysis(
        bool hasBoundaryRoot,
        bool boundaryRootHasErrorContent,
        bool boundaryRootIsKeyed,
        Location? boundaryRootLocation,
        ImmutableArray<RazorMarkupRegion> htmlInteractiveRegions,
        ImmutableArray<RazorComponentRoot> componentRoots)
    {
        HasBoundaryRoot = hasBoundaryRoot;
        BoundaryRootHasErrorContent = boundaryRootHasErrorContent;
        BoundaryRootIsKeyed = boundaryRootIsKeyed;
        BoundaryRootLocation = boundaryRootLocation;
        HtmlInteractiveRegions = htmlInteractiveRegions;
        ComponentRoots = componentRoots;
    }

    public bool HasBoundaryRoot { get; }

    public bool BoundaryRootHasErrorContent { get; }

    public bool BoundaryRootIsKeyed { get; }

    public Location? BoundaryRootLocation { get; }

    public ImmutableArray<RazorMarkupRegion> HtmlInteractiveRegions { get; }

    public ImmutableArray<RazorComponentRoot> ComponentRoots { get; }
}

internal readonly struct RazorMarkupRegion
{
    public RazorMarkupRegion(InteractiveRenderRegionKind kind, string tagName, Location diagnosticLocation)
    {
        Kind = kind;
        TagName = tagName;
        DiagnosticLocation = diagnosticLocation;
    }

    public InteractiveRenderRegionKind Kind { get; }

    public string TagName { get; }

    public Location DiagnosticLocation { get; }
}

internal readonly struct RazorComponentRoot
{
    public RazorComponentRoot(string tagName, Location rootLocation, Location? bindingLocation)
    {
        TagName = tagName;
        RootLocation = rootLocation;
        BindingLocation = bindingLocation;
    }

    public string TagName { get; }

    public Location RootLocation { get; }

    public Location? BindingLocation { get; }
}
