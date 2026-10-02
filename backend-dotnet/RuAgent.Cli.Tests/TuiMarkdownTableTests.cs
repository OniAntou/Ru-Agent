using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiMarkdownTableTests
{
    private static IDisposable Level() => TuiPalette.PinLevel(TuiColorLevel.None);

    [Fact]
    public void RenderLine_TableSeparator_KeepsColumns()
    {
        var md = new TuiMarkdown();
        var rows = md.RenderLine("| a | b |", 40);

        Assert.NotEmpty(rows);
        Assert.Contains("a", rows[0]);
    }

    [Fact]
    public void RenderLine_TableRow_JoinsTrimmedCellsWithAColumnSeparator()
    {
        using var _ = Level();

        var rows = new TuiMarkdown().RenderLine("| id | name |", 40);

        Assert.Equal("id │ name", Assert.Single(rows));
    }

    [Fact]
    public void RenderLine_TableRow_RendersInlineMarkupInsideCells()
    {
        using var _ = Level();

        var rows = new TuiMarkdown().RenderLine("| **bold** | `code` |", 40);

        Assert.Equal("bold │ code", Assert.Single(rows));
    }

    [Fact]
    public void RenderLine_TableRowsSplitAcrossStreamedCalls_KeepTheirColumns()
    {
        using var _ = Level();
        var md = new TuiMarkdown();

        var header = Assert.Single(md.RenderLine("| a | b |", 40));
        var row = Assert.Single(md.RenderLine("| 1 | 2 |", 40));

        Assert.Equal("a │ b", header);
        Assert.Equal("1 │ 2", row);
        Assert.False(md.InFence);
    }

    [Fact]
    public void TableRowsEndOnAPipe_WhichTheDeltaHoldbackDoesNotKeepBack()
    {
        Assert.Equal(0, TuiMarkdown.Holdback("| a | b |"));
    }

    [Fact]
    public void RenderLine_SinglePipeStaysProse()
    {
        using var _ = Level();

        var rows = new TuiMarkdown().RenderLine("| not a table", 40);

        Assert.Equal("| not a table", Assert.Single(rows));
    }

    [Fact]
    public void RenderLine_TableInsideAFence_StaysCode()
    {
        using var _ = Level();
        var md = new TuiMarkdown();
        Assert.Empty(md.RenderLine("```", 40));

        var row = Assert.Single(md.RenderLine("| a | b |", 40));

        Assert.Contains("| a | b |", row, StringComparison.Ordinal);
        Assert.True(md.InFence);
    }

    [Theory]
    [InlineData("  - nested")]
    [InlineData("  * nested")]
    [InlineData("  + nested")]
    public void RenderLine_NestedListItem_GetsTwoExtraSpacesOfIndent(string line)
    {
        using var _ = Level();

        var rows = new TuiMarkdown().RenderLine(line, 40);

        Assert.Equal("    •  nested", Assert.Single(rows));
    }

    [Theory]
    [InlineData("    - deeper")]
    [InlineData("    * deeper")]
    public void RenderLine_DeeperNesting_FallsBackToPlainIndentedText(string line)
    {
        using var _ = Level();

        var rows = new TuiMarkdown().RenderLine(line, 40);

        Assert.Equal(line, Assert.Single(rows));
    }

    [Fact]
    public void RenderLine_TopLevelBulletsKeepTheirExistingMarker()
    {
        using var _ = Level();

        var rows = new TuiMarkdown().RenderLine("- first", 40);

        Assert.Equal("  •  first", Assert.Single(rows));
    }

    [Fact]
    public void RenderLine_LongTable_WrapsWithinTheWidth()
    {
        using var _ = Level();

        var rows = new TuiMarkdown().RenderLine("| alpha | beta | gamma | delta |", 20);

        Assert.True(rows.Count > 1);
        Assert.All(rows, row => Assert.True(TuiText.VisibleWidth(row) <= 20, row));
    }
}
