using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The context meter's composition: fill rounding, threshold tones, percent
/// precision, and the cluster parts both header engines render from.
/// </summary>
public sealed class TuiUsageMeterTests
{
    [Theory]
    [InlineData(0.4, 1)]
    [InlineData(4.32, 1)]
    [InlineData(12.5, 1)]
    [InlineData(20, 2)]
    [InlineData(50, 4)]
    [InlineData(62.5, 5)]
    [InlineData(99.9, 8)]
    [InlineData(100, 8)]
    [InlineData(250, 8)]
    public void FillCells_RoundsUpAndKeepsAtLeastOneLitCell(double percent, int expected)
    {
        Assert.Equal(expected, TuiUsageMeter.FillCells(percent));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void FillCells_ZeroOrNegativeReadsEmpty(double percent)
    {
        Assert.Equal(0, TuiUsageMeter.FillCells(percent));
    }

    [Theory]
    [InlineData(4.32, TuiToken.Bright)]
    [InlineData(69.99, TuiToken.Bright)]
    [InlineData(70, TuiToken.Warn)]
    [InlineData(89.99, TuiToken.Warn)]
    [InlineData(90, TuiToken.Err)]
    [InlineData(999, TuiToken.Err)]
    public void FillTone_ShiftsAtItsNamedThresholds(double percent, TuiToken expected)
    {
        Assert.Equal(expected, TuiUsageMeter.FillTone(percent));
    }

    [Theory]
    [InlineData(4.32, "4.32%")]
    [InlineData(4.3, "4.30%")]
    [InlineData(100, "100.00%")]
    [InlineData(999, "999.00%")]
    public void FormatPercent_KeepsTwoDecimals(double percent, string expected)
    {
        Assert.Equal(expected, TuiUsageMeter.FormatPercent(percent));
    }

    [Fact]
    public void Compose_OrdersTokensMeterPercentCost()
    {
        var parts = TuiUsageMeter.Compose(39413, 20, 0.29m);

        Assert.Equal(4, parts.Count);
        Assert.Equal(new TuiUsagePart("39,413", TuiToken.Meta), parts[0]);
        Assert.True(parts[1].IsMeter);
        Assert.Equal(2, parts[1].FillCells);
        Assert.Equal(TuiToken.Bright, parts[1].FillTone);
        Assert.Equal(new TuiUsagePart("20.00%", TuiToken.Meta), parts[2]);
        Assert.Equal(new TuiUsagePart("($0.29)", TuiToken.Meta), parts[3]);
    }

    [Fact]
    public void Compose_OmitsUnpublishedMembers()
    {
        Assert.Empty(TuiUsageMeter.Compose(null, null, null));
        Assert.Empty(TuiUsageMeter.Compose(0, null, null));
        Assert.Empty(TuiUsageMeter.Compose(-1, null, null));

        // A meter without a token tally keeps its own pair: meter + percent.
        var percentOnly = TuiUsageMeter.Compose(0, 20, null);
        Assert.Equal(2, percentOnly.Count);
        Assert.True(percentOnly[0].IsMeter);

        var tokensOnly = TuiUsageMeter.Compose(39413, null, null);
        Assert.Single(tokensOnly);
        Assert.Equal("39,413", tokensOnly[0].Text);
    }

    [Fact]
    public void VisibleWidth_CountsTheBarAndItsTighterSeparator()
    {
        var parts = TuiUsageMeter.Compose(39413, 20, 0.29m);

        // "39,413" · 2 · bar · 1 · "20.00%" · 2 · "($0.29)".
        Assert.Equal(
            6 + 2 + TuiUsageMeter.BarWidth + 1 + 6 + 2 + 7,
            TuiUsageMeter.VisibleWidth(parts));
        Assert.Equal(0, TuiUsageMeter.VisibleWidth(Array.Empty<TuiUsagePart>()));
    }

    [Fact]
    public void SeparatorAfter_KeepsThePercentBesideItsMeter()
    {
        var parts = TuiUsageMeter.Compose(39413, 20, 0.29m);

        Assert.Equal(2, TuiUsageMeter.SeparatorAfter(parts[0]));
        Assert.Equal(1, TuiUsageMeter.SeparatorAfter(parts[1]));
        Assert.Equal(2, TuiUsageMeter.SeparatorAfter(parts[2]));
    }
}
