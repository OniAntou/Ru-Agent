using Markdig.Syntax;
using RuApp.Controls;
using Xunit;

namespace RuApp.Tests;

public sealed class MarkdownRenderPlanTests
{
    private static IReadOnlyList<MarkdownRenderKey> Keys(string text) =>
        MarkdownRenderPlan.Keys(MarkdownEngine.Parse(text), text);

    [Fact]
    public void Streaming_tail_retains_completed_tables_code_and_paragraphs()
    {
        const string before = "# Heading\n\nStable paragraph.\n\n| A | B |\n|---|---|\n| one | two |\n\n```cs\nvar a = 1;\n```\n\nGrowing";
        var previous = Keys(before);
        var after = Keys(before + " tail");
        // Markdig appends a hidden LinkReferenceDefinitionGroup after the live
        // paragraph. The four visible blocks preceding that paragraph survive.
        Assert.Equal(new[] { "HeadingBlock", "ParagraphBlock", "Table", "FencedCodeBlock" },
            previous.Take(4).Select(key => key.Kind.Name));
        Assert.Equal(4, MarkdownRenderPlan.RetainedPrefix(previous, after));
    }

    [Fact]
    public void Retry_replaces_only_the_changed_suffix()
    {
        var previous = Keys("Stable\n\nFailed answer\n\nRemoved block");
        var next = Keys("Stable\n\nReplacement");
        Assert.Equal(1, MarkdownRenderPlan.RetainedPrefix(previous, next));
        Assert.Equal(0, MarkdownRenderPlan.RetainedPrefix(previous, Keys(string.Empty)));
    }

    [Fact]
    public void Reference_link_resolution_invalidates_an_earlier_unchanged_paragraph()
    {
        const string source = "Read [docs][target].\n\nTail";
        Assert.Equal(0, MarkdownRenderPlan.RetainedPrefix(Keys(source),
            Keys(source + "\n\n[target]: https://example.test/docs")));
    }

    [Theory]
    [InlineData("- Run:\n\n  ```powershell\n    npm run build\n  ```\n\n  > result\n")]
    [InlineData("```cs\n    var a = 1;\n" + "long ## 1. code stays literal\n" + "```\n")]
    [InlineData("~~~text\n" + "    preserve indentation\n~~~\n")]
    [InlineData("    indented code\n\nordinary  \n")]
    public void Normalization_preserves_code_list_indentation_and_hard_breaks(string source) =>
        Assert.Equal(source, MarkdownRenderPlan.PrepareSource(source));

    [Fact]
    public void Nested_list_keeps_its_code_block_after_normalization()
    {
        var document = MarkdownEngine.Parse(MarkdownRenderPlan.PrepareSource("- Run:\n\n  ```sh\n  npm run build\n  ```\n"));
        var list = Assert.IsType<ListBlock>(document[0]);
        var item = Assert.IsType<ListItemBlock>(list[0]);
        Assert.Contains(item, block => block is FencedCodeBlock);
    }

    [Fact]
    public void Long_fenced_code_cannot_trigger_legacy_heading_recovery()
    {
        var source = "```text\n" + new string('x', 500) + " ## 1. literal code\n```";
        Assert.Equal(source, MarkdownRenderPlan.PrepareSource(source));
    }
}
