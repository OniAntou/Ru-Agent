using RuApp.Controls;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Code tinting must never lose or reorder source text: every test asserts the
/// spans rejoin to the input byte-for-byte, then checks the interesting kinds.
/// </summary>
public sealed class CodeHighlighterTests
{
    [Fact]
    public void Csharp_classifies_keywords_strings_comments_and_numbers()
    {
        const string code = "public class A // hi\n{\n  string s = \"x\";\n  int n = 42;\n}";
        var spans = CodeHighlighter.Highlight(code, "csharp");

        Assert.Equal(code, string.Concat(spans.Select(span => span.Text)));
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Keyword && span.Text == "public");
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Comment && span.Text.StartsWith("//", StringComparison.Ordinal));
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.String && span.Text == "\"x\"");
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Number && span.Text == "42");
    }

    [Theory]
    [InlineData("cs")]
    [InlineData("c#")]
    [InlineData("ps1")]
    [InlineData("yml")]
    public void Language_aliases_resolve(string language)
    {
        var spans = CodeHighlighter.Highlight("public x = 1 # c", language);

        Assert.Equal("public x = 1 # c", string.Concat(spans.Select(span => span.Text)));
        Assert.Contains(spans, span => span.Kind != CodeHighlighter.SpanKind.Plain);
    }

    [Fact]
    public void Unknown_language_stays_plain()
    {
        var spans = CodeHighlighter.Highlight("hello world", "klingon");

        var single = Assert.Single(spans);
        Assert.Equal(CodeHighlighter.SpanKind.Plain, single.Kind);
        Assert.Equal("hello world", single.Text);
    }

    [Fact]
    public void Diff_marks_added_removed_and_hunks()
    {
        const string code = "@@ -1 +1 @@\n+added\n-removed\n context\n";
        var spans = CodeHighlighter.Highlight(code, "diff");

        Assert.Equal(code, string.Concat(spans.Select(span => span.Text)));
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Added);
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Removed);
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.HunkHeader);
    }

    [Fact]
    public void Xml_marks_tags_and_comments()
    {
        const string code = "<!-- note -->\n<Grid><TextBlock Text=\"hi\" /></Grid>";
        var spans = CodeHighlighter.Highlight(code, "xml");

        Assert.Equal(code, string.Concat(spans.Select(span => span.Text)));
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Comment);
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Keyword && span.Text.StartsWith("<", StringComparison.Ordinal));
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.String && span.Text == "\"hi\"");
    }

    [Fact]
    public void Empty_code_yields_no_spans()
    {
        Assert.Empty(CodeHighlighter.Highlight(string.Empty, "csharp"));
        Assert.Empty(CodeHighlighter.Highlight(null, "csharp"));
    }
}
