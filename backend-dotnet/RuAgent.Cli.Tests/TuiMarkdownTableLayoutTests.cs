using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiMarkdownTableLayoutTests
{
    private static readonly string[] Gold = [
        "Thương hiệu | Mua vào | Bán ra",
        "--- | ---: | ---:",
        "SJC | 141,3 tr | 144,3 tr",
        "Bảo Tín Minh Châu | 139,4 tr | 144,3 tr"];

    [Fact]
    public void OptionalOuterPipesAlignVietnameseColumns()
    {
        Assert.True(TuiMarkdownTable.TryRender(Gold, 0, 80, out var consumed, out var rows));
        Assert.Equal(4, consumed);
        Assert.Equal(rows[0].IndexOf('│'), rows[2].IndexOf('│'));
        Assert.Equal(rows[2].IndexOf('│'), rows[3].IndexOf('│'));
        Assert.Contains("Bảo Tín Minh Châu", rows[3]);
        Assert.Contains("┼", rows[1]);
    }

    [Theory]
    [InlineData(18)]
    [InlineData(32)]
    public void NarrowTablesPreserveAllValuesInsteadOfTruncating(int width)
    {
        Assert.True(TuiMarkdownTable.TryRender(Gold, 0, width, out _, out var rows));
        Assert.All(rows, row => Assert.True(TuiText.VisibleWidth(row) <= width));
        Assert.Contains("144,3 tr", string.Join("\n", rows));
    }

    [Fact]
    public void NumericColumnsRightAlignWithoutColonMarkers()
    {
        string[] table =
        [
            "Mã | Mua | Bán",
            "--- | --- | ---",
            "SJC | 139.000 | 142.000",
            "PNJ | 138.500 | 141.000",
        ];

        Assert.True(TuiMarkdownTable.TryRender(table, 0, 80, out _, out var rows));
        var first = rows.First(row => row.Contains("139.000", StringComparison.Ordinal));
        var second = rows.First(row => row.Contains("138.500", StringComparison.Ordinal));
        Assert.Equal(
            first.IndexOf("139.000", StringComparison.Ordinal) + "139.000".Length,
            second.IndexOf("138.500", StringComparison.Ordinal) + "138.500".Length);
    }

    [Fact]
    public void CenterMarkersCenterTheCell()
    {
        string[] table = ["A | B", ":---: | ---", "x | y"];

        Assert.True(TuiMarkdownTable.TryRender(table, 0, 40, out _, out var rows));
        var body = rows.First(row => row.Contains('x', StringComparison.Ordinal));
        Assert.StartsWith(" ", body, StringComparison.Ordinal);
    }

    [Fact]
    public void OrdinaryPipeProseIsNotMistakenForATable()
    {
        Assert.False(TuiMarkdownTable.TryRender(["a | b", "regular prose"], 0, 80, out _, out _));
    }
}
