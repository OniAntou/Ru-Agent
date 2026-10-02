using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiScreenLayoutTests
{
    [Fact]
    public void Compute_120x40_ReservesHeaderAndFooter()
    {
        var layout = TuiScreenLayout.Compute(columns: 120, rows: 40);

        Assert.Equal(0, layout.HeaderTop);
        Assert.Equal(1, layout.HeaderHeight);
        Assert.Equal(3, layout.FooterHeight);
        Assert.Equal(37, layout.FooterTop);
        Assert.Equal(1, layout.TranscriptTop);
        Assert.Equal(36, layout.TranscriptHeight);
    }

    [Fact]
    public void Compute_80x24_MinimumScreen_Leaves20TranscriptRows()
    {
        var layout = TuiScreenLayout.Compute(columns: 80, rows: 24);

        Assert.Equal(20, layout.TranscriptHeight);
    }

    [Fact]
    public void Compute_RowsExactlyHeaderPlusFooter_LeavesZeroTranscriptRows()
    {
        var layout = TuiScreenLayout.Compute(columns: 80, rows: 4);

        Assert.Equal(0, layout.TranscriptHeight);
        Assert.Equal(1, layout.FooterTop);
    }

    [Fact]
    public void Compute_TinyRows_NeverReturnsNegativeGeometry()
    {
        var layout = TuiScreenLayout.Compute(columns: 80, rows: 1);

        Assert.True(layout.TranscriptHeight >= 0);
        Assert.True(layout.FooterTop >= 0);
    }

    [Fact]
    public void Compute_Columns_PassThroughUnchanged()
    {
        var layout = TuiScreenLayout.Compute(columns: 132, rows: 40);

        Assert.Equal(132, layout.Columns);
    }
}
