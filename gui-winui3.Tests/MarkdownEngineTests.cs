using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
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
    public void Heading_link_targets_are_not_content_blocks()
    {
        var document = MarkdownEngine.Parse("## One\n\ntext\n\n## Two\n\n[ref]: https://example.com");

        // Each heading files a definition; they must render as nothing, or every
        // heading adds an empty line under the reply.
        var group = Assert.Single(document.OfType<LinkReferenceDefinitionGroup>());
        Assert.True(group.Count >= 3);
        Assert.True(MarkdownEngine.IsLinkTargetsOnly(group));
        Assert.All(group, definition => Assert.True(MarkdownEngine.IsLinkTargetsOnly(definition)));
        Assert.All(document.Where(block => block is not LinkReferenceDefinitionGroup),
            block => Assert.False(MarkdownEngine.IsLinkTargetsOnly(block)));
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
    public void Plain_entity_and_line_break_html_stay_visible()
    {
        var document = MarkdownEngine.Parse("AT&amp;T<br>next");

        var entity = Assert.Single(document.Descendants().OfType<HtmlEntityInline>());
        Assert.Equal("&", MarkdownBlock.EntityText(entity));
        var lineBreak = Assert.Single(document.Descendants().OfType<HtmlInline>());
        Assert.True(MarkdownBlock.IsLineBreakHtmlTag(lineBreak.Tag));
    }

    [Fact]
    public void Table_cells_decode_html_entities()
    {
        var document = MarkdownEngine.Parse("| Name |\n|---|\n| AT&amp;T |");
        var cell = document.Descendants().OfType<TableCell>().Last();

        Assert.Equal("AT&T", MarkdownEngine.TableCellText(cell));
    }

    [Fact]
    public void Ordered_list_keeps_its_source_start_number()
    {
        var document = MarkdownEngine.Parse("3. third\n4. fourth");
        var list = Assert.Single(document.Descendants().OfType<ListBlock>());

        Assert.Equal(3, MarkdownBlock.OrderedListStart(list));
    }

    [Fact]
    public void Table_columns_map_declared_alignment()
    {
        var document = MarkdownEngine.Parse("| l | c | r |\n|:--|:-:|--:|\n| 1 | 2 | 3 |");
        var columns = Assert.Single(document.Descendants().OfType<Table>()).ColumnDefinitions;

        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Left, MarkdownBlock.TableColumnAlignment(columns[0]));
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Center, MarkdownBlock.TableColumnAlignment(columns[1]));
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Right, MarkdownBlock.TableColumnAlignment(columns[2]));
    }

    [Fact]
    public void Math_and_footnote_references_keep_their_source_markers()
    {
        var math = Assert.Single(
            MarkdownEngine.Parse("inline $x^2$").Descendants().OfType<MathInline>());
        Assert.Equal("$x^2$", MarkdownBlock.MathLiteral(math));

        var footnote = MarkdownEngine.Parse("claim[^1]\n\n[^1]: note")
            .Descendants().OfType<FootnoteLink>().First(link => !link.IsBackLink);
        Assert.Equal("[^1]", MarkdownBlock.FootnoteMarker(footnote));
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

    [Fact]
    public void External_link_destinations_are_shown_before_a_click()
    {
        // Model output picks both the label and the target, so every external
        // destination goes through the helper that sets the address tooltip.
        var code = GuiSource.Text("Controls", "MarkdownBlock.cs");
        Assert.Contains("ToolTipService.SetToolTip(hyperlink, uri.AbsoluteUri)", code, StringComparison.Ordinal);
        Assert.Equal(1, code.Split("NavigateUri = ").Length - 1);
    }
}
