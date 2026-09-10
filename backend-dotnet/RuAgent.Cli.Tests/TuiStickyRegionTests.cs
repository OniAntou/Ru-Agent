using RuAgent.Cli;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiStickyRegionTests
{
    private static TuiStickyRegion Create(StringWriter writer)
    {
        TuiPalette.Level = TuiColorLevel.None;
        return new TuiStickyRegion(new TuiSurface(writer));
    }

    [Theory]
    [InlineData(30, 4, 1, 26)]
    [InlineData(10, 4, 1, 6)]
    [InlineData(6, 4, 1, 2)]
    [InlineData(5, 4, 0, 0)]
    [InlineData(0, 4, 0, 0)]
    [InlineData(30, 0, 0, 0)]
    public void Compute_KeepsAtLeastTwoTranscriptRows(int rows, int footerHeight, int expectedTop, int expectedBottom)
    {
        Assert.Equal((expectedTop, expectedBottom), TuiStickyRegion.Compute(rows, footerHeight));
    }

    [Fact]
    public void ResolveFooterHeight_RefusesWhenTheViewportIsTooShort()
    {
        Assert.Equal(4, TuiStickyRegion.ResolveFooterHeight(30, 4));
        Assert.Equal(0, TuiStickyRegion.ResolveFooterHeight(5, 4));
    }

    [Fact]
    public void Enter_ReservesTheRegionWithDecstbm()
    {
        var writer = new StringWriter();
        var region = Create(writer);

        Assert.True(region.Enter(30, 4));

        Assert.Equal("\u001b[1;26r", writer.ToString());
        Assert.True(region.Active);
    }

    [Fact]
    public void Enter_IsRejectedOnAShortViewport()
    {
        var writer = new StringWriter();
        var region = Create(writer);

        Assert.False(region.Enter(5, 4));

        Assert.Equal(string.Empty, writer.ToString());
        Assert.False(region.Active);
    }

    [Fact]
    public void Exit_ResetsTheScrollingRegionAndParksTheCursor()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        writer.GetStringBuilder().Clear();

        region.Exit();
        region.Exit();

        var text = writer.ToString();
        Assert.StartsWith("\u001b[r", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[26;1H", text, StringComparison.Ordinal);
        Assert.False(region.Active);
        Assert.Equal(0, region.FooterFirst);
    }

    [Theory]
    [InlineData(27, 40, "\u001b[26;1H\u001b[J")]
    [InlineData(27, 30, "\u001b[26;1H\u001b[J")]
    [InlineData(4, 30, "\u001b[3;1H\u001b[J")]
    [InlineData(20, 20, "\u001b[19;1H\u001b[J")]
    public void ComposeClearDown_StartsOnTheRowAboveTheFooter(int firstFooterRow, int liveRows, string expected)
    {
        Assert.Equal(expected, TuiStickyRegion.ComposeClearDown(firstFooterRow, liveRows));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(27, 0)]
    [InlineData(1, 30)]
    [InlineData(27, 20)]
    public void ComposeClearDown_NothingToErase(int firstFooterRow, int liveRows)
    {
        Assert.Equal(string.Empty, TuiStickyRegion.ComposeClearDown(firstFooterRow, liveRows));
    }

    [Fact]
    public void Exit_ParksTheCursorWhereTheFooterUsedToStart()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        writer.GetStringBuilder().Clear();

        region.Exit();

        // Footer owned rows 27..30 (transcript ends at 26), so the next append lands on 26.
        Assert.Contains("\u001b[26;1H", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reapply_ResetsBeforeInstallingTheNewRegion()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        writer.GetStringBuilder().Clear();

        Assert.True(region.Reapply(20, 4));

        var text = writer.ToString();
        Assert.StartsWith("\u001b[r", text, StringComparison.Ordinal);
        Assert.EndsWith("\u001b[1;16r", text, StringComparison.Ordinal);
        Assert.Equal(20, region.Rows);
        Assert.Equal(17, region.FooterFirst);
    }

    [Fact]
    public void Reapply_LeavesStickyModeWhenTheViewportIsTooShort()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        writer.GetStringBuilder().Clear();

        Assert.False(region.Reapply(4, 4));

        Assert.False(region.Active);
        Assert.Contains("\u001b[r", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Reapply_AfterExit_EntersWithoutClearing()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        region.Exit();
        writer.GetStringBuilder().Clear();

        Assert.True(region.Reapply(24, 3));

        Assert.Equal("\u001b[1;21r", writer.ToString());
    }

    [Fact]
    public void Paint_AfterExit_WritesNothing()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        region.Exit();
        writer.GetStringBuilder().Clear();

        region.Paint(["stale"], 40);

        Assert.Equal(string.Empty, writer.ToString());
    }

    [Fact]
    public void Paint_ClearsEveryReservedRow()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        writer.GetStringBuilder().Clear();

        region.Paint(["a"], 40);

        var text = writer.ToString();
        Assert.Contains("\u001b[27;1H", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[28;1H", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[29;1H", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[30;1H", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Paint_RepaintsFooterRowsAndParksTheCursorAboveThem()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        writer.GetStringBuilder().Clear();

        region.Paint(["row-a", "row-b"], 40);

        var text = writer.ToString();
        Assert.Contains("\u001b[27;1H", text, StringComparison.Ordinal);
        Assert.Contains("\u001b[28;1H", text, StringComparison.Ordinal);
        Assert.Contains("row-a", text, StringComparison.Ordinal);
        Assert.Contains("row-b", text, StringComparison.Ordinal);
        Assert.EndsWith("\u001b[26;1H", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Paint_ClipsFooterLinesToTheViewport()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        writer.GetStringBuilder().Clear();

        region.Paint([new string('x', 100)], 20);

        var text = writer.ToString();
        var painted = text[(text.IndexOf("\u001b[2K", StringComparison.Ordinal) + 4)..];
        painted = painted[..painted.IndexOf("\u001b[", StringComparison.Ordinal)];
        Assert.Equal(20, TuiText.VisibleWidth(painted));
    }

    [Fact]
    public void Paint_ParksTheCaretInsideTheComposerWhenAsked()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        writer.GetStringBuilder().Clear();

        region.Paint(["status", "composer", "hint"], 40, caretRowInFooter: 2, caretColumn: 9);

        Assert.EndsWith("\u001b[28;9H", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Paint_ClipsTheCaretRowToTheRegion()
    {
        var writer = new StringWriter();
        var region = Create(writer);
        region.Enter(30, 4);
        writer.GetStringBuilder().Clear();

        region.Paint(["status"], 40, caretRowInFooter: 99, caretColumn: 3);

        Assert.EndsWith("\u001b[30;3H", writer.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30, true, 30, false)]
    [InlineData(30, true, 47, true)]
    [InlineData(30, true, 20, true)]
    [InlineData(30, false, 0, false)]
    public void LayoutIsStale_ComparesTheRegionWithTheLiveHeight(int regionRows, bool hasLiveSize, int liveRows, bool expected)
    {
        Assert.Equal(expected, TuiStickyRegion.LayoutIsStale(regionRows, hasLiveSize, liveRows));
    }

    [Fact]
    public void Paint_IsIgnoredWhileInactive()
    {
        var writer = new StringWriter();
        var region = Create(writer);

        region.Paint(["row"], 40);

        Assert.Equal(string.Empty, writer.ToString());
    }
}

public sealed class PendingPromptSlotTests
{
    [Fact]
    public void TryQueue_KeepsTheFirstPromptOnly()
    {
        var slot = new PendingPromptSlot();

        Assert.True(slot.TryQueue("first"));
        Assert.False(slot.TryQueue("second"));
        Assert.True(slot.HasValue);
        Assert.Equal("first", slot.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryQueue_RejectsEmptyPrompts(string? prompt)
    {
        var slot = new PendingPromptSlot();

        Assert.False(slot.TryQueue(prompt));
        Assert.False(slot.HasValue);
    }

    [Fact]
    public void Take_ReturnsAndClears()
    {
        var slot = new PendingPromptSlot();
        slot.TryQueue("queued");

        Assert.Equal("queued", slot.Take());
        Assert.False(slot.HasValue);
        Assert.Null(slot.Take());
    }

    [Fact]
    public void Clear_EmptiesTheSlot()
    {
        var slot = new PendingPromptSlot();
        slot.TryQueue("queued");

        slot.Clear();

        Assert.False(slot.HasValue);
    }
}
