using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using RuApp.Controls;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Chat rendering runs on a full Markdown engine, not the old hand-rolled
/// line parser. These pin the document shapes the renderer depends on:
/// single newlines stay one paragraph, and tables, task lists,
/// strikethrough and autolinks parse instead of showing up raw.
/// </summary>
public sealed class MarkdownEngineTests
{
    [Theory]
    [InlineData("**SJC**", "SJC")]
    [InlineData("[docs](https://example.com)", "docs")]
    [InlineData("*run **`npm install`***", "run npm install")]
    public void Table_cells_keep_nested_inline_content(string markdown, string expected)
    {
        var document = MarkdownEngine.Parse($"| Name |\n|---|\n| {markdown} |");
        var cell = document.Descendants().OfType<TableCell>().Last();
        Assert.Equal(expected, MarkdownEngine.TableCellText(cell));
    }

    [Fact]
    public void Single_newlines_stay_in_one_paragraph()
    {
        var document = MarkdownEngine.Parse("first line\nsecond line\n\nnext paragraph");

        var paragraphs = document.Descendants().OfType<ParagraphBlock>().ToArray();
        Assert.Equal(2, paragraphs.Length);
    }

    [Fact]
    public void Tables_parse_with_header_and_cells()
    {
        var document = MarkdownEngine.Parse("| a | b |\n|---|---|\n| 1 | 2 |");

        var table = Assert.Single(document.Descendants().OfType<Table>());
        var rows = table.Descendants().OfType<TableRow>().ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(2, rows[0].Descendants().OfType<TableCell>().Count());
    }

    [Fact]
    public void Task_list_items_carry_checked_state()
    {
        var document = MarkdownEngine.Parse("- [ ] open\n- [x] done");

        var tasks = document.Descendants().OfType<TaskList>().ToArray();
        Assert.Equal(2, tasks.Length);
        Assert.False(tasks[0].Checked);
        Assert.True(tasks[1].Checked);
    }

    [Fact]
    public void Strikethrough_parses_as_emphasis()
    {
        var document = MarkdownEngine.Parse("gone ~~removed~~ here");

        var emphasis = document.Descendants().OfType<EmphasisInline>()
            .FirstOrDefault(item => item.DelimiterChar == '~');
        Assert.NotNull(emphasis);
    }

    [Fact]
    public void Autolinks_parse_to_links()
    {
        var document = MarkdownEngine.Parse("see <https://example.com> now");

        var auto = Assert.Single(
            document.Descendants().OfType<Markdig.Syntax.Inlines.AutolinkInline>());
        Assert.Contains("example.com", auto.Url ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void Fenced_code_blocks_keep_their_text()
    {
        var document = MarkdownEngine.Parse("```cs\nvar x = 1;\n```");

        var code = Assert.Single(document.Descendants().OfType<FencedCodeBlock>());
        Assert.Contains("var x = 1;", code.Lines.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Table_cells_flatten_to_tsv_for_the_copy_button()
    {
        var tsv = MarkdownBlock.TableCellsToTsv(
            new List<IReadOnlyList<string>>
            {
                new[] { "a", "b" },
                new[] { "1", "x\ty" },
            },
            columns: 3);

        Assert.Equal("a\tb\t\n1\tx y\t", tsv);
    }
}
