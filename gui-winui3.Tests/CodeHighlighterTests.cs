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
    public void Calls_and_types_get_their_own_kinds()
    {
        const string code = "server = ThreadingHTTPServer(('', 0), Handler)\nprint(len(x))";
        var spans = CodeHighlighter.Highlight(code, "python");

        Assert.Equal(code, string.Concat(spans.Select(span => span.Text)));
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Function && span.Text == "ThreadingHTTPServer");
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Function && span.Text == "print");
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Type && span.Text == "Handler");
        Assert.Contains(spans, span => span.Kind == CodeHighlighter.SpanKind.Plain && span.Text == "server");
    }

    [Fact]
    public void Dashes_join_identifiers_only_in_shell_like_languages()
    {
        Assert.Contains(CodeHighlighter.Highlight("Get-Item x", "powershell"), span => span.Text == "Get-Item");
        Assert.DoesNotContain(CodeHighlighter.Highlight("a-b", "csharp"), span => span.Text == "a-b");
    }

    [Theory]
    [InlineData("server.py", "python")]
    [InlineData("src/App.CS", "csharp")]
    [InlineData("index.tsx", "javascript")]
    [InlineData("main.rs", "rust")]
    [InlineData("App.xaml", "xml")]
    [InlineData("notes.txt", null)]
    [InlineData("Makefile", null)]
    [InlineData(null, null)]
    public void Files_map_to_languages_by_extension(string? fileName, string? language) =>
        Assert.Equal(language, CodeHighlighter.LanguageForFile(fileName));

    [Fact]
    public void Lines_keep_a_comment_that_spans_them_and_drop_the_breaks()
    {
        var lines = CodeHighlighter.HighlightLines("int a; /* one\r\ntwo */ int b;", "csharp");

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines[1], span => span.Kind == CodeHighlighter.SpanKind.Comment && span.Text == "two */");
        Assert.DoesNotContain(lines.SelectMany(line => line), span => span.Text.Contains('\n') || span.Text.Contains('\r'));
    }

    [Fact]
    public void The_code_preview_numbers_lines_and_names_the_language()
    {
        var view = CodePreviewModel.Build("import os\nprint(os.name)\n", @"C:\work\tool.py");

        Assert.Equal("1\n2", view.LineNumbers);
        Assert.Equal(2, view.Lines.Count);
        Assert.Equal("Python · 2 lines", view.Description);
        Assert.Contains(view.Lines[0], span => span.Kind == CodeHighlighter.SpanKind.Keyword && span.Text == "import");
    }

    [Fact]
    public void A_huge_or_unknown_file_previews_as_plain_text()
    {
        var huge = CodePreviewModel.Build(new string('x', CodePreviewModel.MaxHighlightedChars + 1), "big.cs");
        Assert.All(huge.Lines.SelectMany(line => line), span => Assert.Equal(CodeHighlighter.SpanKind.Plain, span.Kind));
        Assert.EndsWith("colors off for large files", huge.Description, StringComparison.Ordinal);

        Assert.Equal("Text · 1 line", CodePreviewModel.Build("hello", "notes.txt").Description);
    }

    [Fact]
    public void Empty_code_yields_no_spans()
    {
        Assert.Empty(CodeHighlighter.Highlight(string.Empty, "csharp"));
        Assert.Empty(CodeHighlighter.Highlight(null, "csharp"));
    }
}
