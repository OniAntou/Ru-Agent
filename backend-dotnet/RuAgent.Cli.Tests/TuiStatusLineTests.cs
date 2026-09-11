using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiStatusLineTests
{
    private static TuiStatusLine Create()
    {
        TuiPalette.Level = TuiColorLevel.None;
        return new TuiStatusLine(new TuiSurface(new StringWriter()));
    }

    [Theory]
    [InlineData(20)]
    [InlineData(40)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(200)]
    public void Compose_AlwaysFillsExactlyTheViewportWidth(int width)
    {
        var status = Create();
        status.SetState(TuiRunState.Tool);
        status.SetActivity("Edit src/app.cs:12-40");
        status.SetElapsed(1.1);
        status.SetCounters(12400, 4);

        Assert.Equal(width, TuiText.VisibleWidth(status.Compose(width)));
    }

    [Fact]
    public void Thinking_row_uses_brand_frame_and_line_elapsed()
    {
        TuiPalette.Level = TuiColorLevel.TrueColor;
        try
        {
            var status = new TuiStatusLine(new TuiSurface(new StringWriter()));
            status.IsLive = true;
            status.SetState(TuiRunState.Thinking);
            status.SetElapsed(1.5);
            var row = status.Compose(80);
            Assert.Equal(80, TuiText.VisibleWidth(row));
            Assert.Contains(TuiPalette.Code(TuiToken.Brand), row);
        }
        finally
        {
            TuiPalette.ResetLevel();
        }
    }

    [Fact]
    public void LiveRow_IsSuppressedInsideTheStickyFooter()
    {
        var status = Create();
        status.IsLive = true;

        Assert.NotNull(status.LiveRow(80));

        status.InFooter = true;
        Assert.Null(status.LiveRow(80));
    }

    [Fact]
    public void Compose_ThinkingShowsSpinnerAndCounters()
    {
        var status = Create();
        status.SetState(TuiRunState.Thinking);
        status.SetElapsed(2.1);
        status.SetCounters(12400, 4);

        var text = status.Compose(80);

        Assert.Contains("Thinking", text, StringComparison.Ordinal);
        Assert.Contains("2.1s", text, StringComparison.Ordinal);
        Assert.DoesNotContain("turn", text, StringComparison.Ordinal);
        Assert.Contains("⇣12.4k", text, StringComparison.Ordinal);
        Assert.Contains("4 tools", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ToolStateShowsTheActivityLabel()
    {
        var status = Create();
        status.SetState(TuiRunState.Tool);
        status.SetActivity("Edit src/app.cs:12-40");
        status.SetElapsed(1.1);

        var text = status.Compose(80);

        Assert.Contains("Edit src/app.cs:12-40", text, StringComparison.Ordinal);
        Assert.Contains("1.1s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_FallsBackToAGenericLabelWithoutActivity()
    {
        var status = Create();
        status.SetState(TuiRunState.Tool);

        Assert.Contains("Running tool", status.Compose(80), StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_DropsCountersOnNarrowTerminals()
    {
        var status = Create();
        status.SetState(TuiRunState.Thinking);
        status.SetElapsed(1.0);
        status.SetCounters(12400, 4);

        var narrow = status.Compose(40);

        Assert.Contains("Thinking", narrow, StringComparison.Ordinal);
        Assert.DoesNotContain("⇣", narrow, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_IdleShowsTheLastRunSummary()
    {
        var status = Create();
        status.SetElapsed(18.4);
        status.SetDetail("3 files changed");
        status.SetCounters(12400, 4);

        var text = status.Compose(80);

        Assert.Contains("✓ idle", text, StringComparison.Ordinal);
        Assert.Contains("18.4s", text, StringComparison.Ordinal);
        Assert.Contains("3 files changed", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_IdleWithoutCountersHintsHelp()
    {
        var status = Create();

        Assert.Contains("/help", status.Compose(80), StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_WaitingApprovalAndFailureUseTheirOwnGlyphs()
    {
        var status = Create();
        status.SetState(TuiRunState.WaitingApproval);
        Assert.Contains("▲ approval needed", status.Compose(80), StringComparison.Ordinal);

        status.SetState(TuiRunState.Failed);
        Assert.Contains("✗ last turn failed", status.Compose(80), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.4, "0.4s")]
    [InlineData(18.44, "18.4s")]
    [InlineData(62, "1m02s")]
    [InlineData(125.6, "2m05s")]
    public void FormatElapsed_UsesSecondsThenMinutes(double seconds, string expected)
    {
        Assert.Equal(expected, TuiStatusLine.FormatElapsed(seconds));
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(999, "999")]
    [InlineData(12400, "12.4k")]
    [InlineData(1000, "1k")]
    public void FormatTokens_CompactsThousands(long tokens, string expected)
    {
        Assert.Equal(expected, TuiStatusLine.FormatTokens(tokens));
    }

    [Fact]
    public void Render_IsSkippedUntilTheRowGoesLive()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new StringWriter();
        var status = new TuiStatusLine(new TuiSurface(writer));

        status.Render(80);
        Assert.Equal(string.Empty, writer.ToString());

        status.IsLive = true;
        status.Render(80);
        Assert.NotEqual(string.Empty, writer.ToString());
    }

    [Fact]
    public void Clear_ReleasesTheRow()
    {
        TuiPalette.Level = TuiColorLevel.None;
        var writer = new StringWriter();
        var status = new TuiStatusLine(new TuiSurface(writer)) { IsLive = true };

        status.Clear(80);

        Assert.False(status.IsLive);
        Assert.Null(status.LiveRow(80));
    }
}
