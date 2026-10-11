using System;
using System.Linq;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using RuApp.Controls;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Emphasis rendering must never stringify a Markdig node: the engine does not
/// override ToString on inlines, so the old default branch printed .NET type
/// names such as Markdig.Syntax.Inlines.CodeInline for a code span nested in
/// bold. These pin the flattened runs instead of the XAML tree, so they run
/// without a UI thread.
/// </summary>
public sealed class MarkdownEmphasisTests
{
    private static EmphasisInline ParseEmphasis(string markdown, char delimiter)
    {
        var document = MarkdownEngine.Parse(markdown);
        return document.Descendants().OfType<EmphasisInline>()
            .First(item => item.DelimiterChar == delimiter);
    }

    private static void AssertNoTypeNameLeak(System.Collections.Generic.IReadOnlyList<MarkdownBlock.EmphasisSegment> segments)
    {
        Assert.NotEmpty(segments);
        Assert.All(segments, segment =>
            Assert.DoesNotContain("Markdig", segment.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void Bold_code_span_flattens_to_visible_mono_text()
    {
        // The exact shape from the transcript: **`standard**` rendered the
        // literal "Markdig.Syntax.Inlines.CodeInline".
        var segments = MarkdownBlock.FlattenEmphasis(ParseEmphasis("preset **`standard`** here", '*'));

        var code = Assert.Single(segments, segment => segment.Mono);
        Assert.Equal("standard", code.Text);
        Assert.True(code.Bold);
        AssertNoTypeNameLeak(segments);
    }

    [Fact]
    public void Italic_code_span_keeps_mono_with_italic()
    {
        var segments = MarkdownBlock.FlattenEmphasis(ParseEmphasis("use *`code`* here", '*'));

        var code = Assert.Single(segments, segment => segment.Mono);
        Assert.Equal("code", code.Text);
        Assert.True(code.Italic);
        Assert.False(code.Bold);
        AssertNoTypeNameLeak(segments);
    }

    [Fact]
    public void Strikethrough_code_span_is_mono_struck_not_swallowed()
    {
        // The old strike branch only kept LiteralInline, so code spans inside
        // ~~...~~ vanished entirely.
        var segments = MarkdownBlock.FlattenEmphasis(ParseEmphasis("gone ~~`code`~~ here", '~'));

        var code = Assert.Single(segments, segment => segment.Mono);
        Assert.Equal("code", code.Text);
        Assert.True(code.Strike);
        AssertNoTypeNameLeak(segments);
    }

    [Fact]
    public void Link_inside_bold_keeps_label_and_target()
    {
        var segments = MarkdownBlock.FlattenEmphasis(ParseEmphasis("see **[label](https://example.com)** now", '*'));

        var linked = Assert.Single(segments, segment => segment.LinkGroup is not null);
        Assert.Equal("label", linked.Text);
        Assert.Equal("https://example.com", linked.LinkUrl);
        Assert.True(linked.Bold);
        AssertNoTypeNameLeak(segments);
    }

    [Fact]
    public void Autolink_and_entity_inside_bold_render_as_text()
    {
        var segments = MarkdownBlock.FlattenEmphasis(ParseEmphasis("see **<https://example.com> and a &amp; b** now", '*'));

        Assert.Contains(segments, segment => segment.Text.Contains("example.com", StringComparison.Ordinal));
        Assert.Contains(segments, segment => segment.Text == "&");
        AssertNoTypeNameLeak(segments);
    }

    [Fact]
    public void Nested_emphasis_combines_styles()
    {
        var segments = MarkdownBlock.FlattenEmphasis(ParseEmphasis("*a **b** c*", '*'));

        var middle = Assert.Single(segments, segment => segment.Text == "b");
        Assert.True(middle.Bold);
        Assert.True(middle.Italic);
        AssertNoTypeNameLeak(segments);
    }

    [Fact]
    public void Plain_bold_prose_keeps_its_shape()
    {
        var segments = MarkdownBlock.FlattenEmphasis(ParseEmphasis("a **plain** word", '*'));

        var bold = Assert.Single(segments, segment => segment.Bold);
        Assert.Equal("plain", bold.Text);
        Assert.False(bold.Mono);
        Assert.False(bold.Italic);
        AssertNoTypeNameLeak(segments);
    }

    [Fact]
    public void Strikethrough_prose_keeps_text_without_bold()
    {
        var segments = MarkdownBlock.FlattenEmphasis(ParseEmphasis("gone ~~removed~~ here", '~'));

        var struck = segments.Where(segment => segment.Strike).ToList();
        Assert.NotEmpty(struck);
        Assert.All(struck, segment => Assert.False(segment.Bold));
        Assert.Contains(struck, segment => segment.Text == "removed");
        AssertNoTypeNameLeak(segments);
    }
}
