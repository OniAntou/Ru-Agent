using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiResizeWatcherTests
{
    [Fact]
    public void Observe_AppliesTheFirstSizeImmediately()
    {
        var watcher = new TuiResizeWatcher();

        Assert.True(watcher.Observe(120, 40, nowMilliseconds: 0));
        Assert.Equal(120, watcher.AppliedColumns);
        Assert.Equal(40, watcher.AppliedRows);
    }

    [Fact]
    public void Observe_IgnoresASizeThatKeepsMoving()
    {
        var watcher = new TuiResizeWatcher(settleMilliseconds: 180);
        watcher.Observe(120, 40, 0);

        // A drag: sizes churn every few milliseconds and nothing may repaint.
        var reported = 0;
        for (var i = 1; i <= 40; i++)
        {
            if (watcher.Observe(120 - i, 40 + (i % 3), i * 5))
            {
                reported++;
            }
        }

        Assert.Equal(0, reported);
    }

    [Fact]
    public void Observe_ReportsOnceAfterTheSizeSettles()
    {
        var watcher = new TuiResizeWatcher(settleMilliseconds: 180);
        watcher.Observe(120, 40, 0);

        // Drag: the last size arrives at t=100.
        watcher.Observe(90, 30, 60);
        watcher.Observe(80, 26, 100);

        Assert.False(watcher.Observe(80, 26, 150));
        Assert.False(watcher.Observe(80, 26, 279));

        Assert.True(watcher.Observe(80, 26, 281));

        // It only ever reports once per settled change.
        Assert.False(watcher.Observe(80, 26, 500));
        Assert.False(watcher.Observe(80, 26, 900));
    }

    [Fact]
    public void Observe_ReportsAgainWhenTheWindowKeepsShrinking()
    {
        var watcher = new TuiResizeWatcher(settleMilliseconds: 180);
        watcher.Observe(120, 40, 0);
        watcher.Observe(80, 26, 0);
        Assert.True(watcher.Observe(80, 26, 200));

        Assert.False(watcher.Observe(60, 20, 400));
        Assert.True(watcher.Observe(60, 20, 700));
        Assert.Equal(60, watcher.AppliedColumns);
        Assert.Equal(20, watcher.AppliedRows);
    }

    [Fact]
    public void Observe_ReportsWhenTheWindowReturnsToAPreviousSize()
    {
        var watcher = new TuiResizeWatcher(settleMilliseconds: 100);
        watcher.Observe(120, 40, 0);
        watcher.Observe(80, 26, 0);
        Assert.True(watcher.Observe(80, 26, 150));

        // Growing back is a real change: the pinned layout has to be rebuilt for 40 rows.
        Assert.False(watcher.Observe(120, 40, 400));
        Assert.True(watcher.Observe(120, 40, 550));
        Assert.Equal(120, watcher.AppliedColumns);

        // And it is still only reported once.
        Assert.False(watcher.Observe(120, 40, 900));
    }

    [Theory]
    [InlineData(0, 40)]
    [InlineData(120, 0)]
    [InlineData(-1, -1)]
    public void Observe_IgnoresGarbageSizes(int columns, int rows)
    {
        var watcher = new TuiResizeWatcher();
        watcher.Observe(120, 40, 0);

        Assert.False(watcher.Observe(columns, rows, 1000));
        Assert.Equal(120, watcher.AppliedColumns);
        Assert.Equal(40, watcher.AppliedRows);
    }

    [Fact]
    public void Observe_WithZeroSettle_ReportsTheNextChangeImmediately()
    {
        var watcher = new TuiResizeWatcher(settleMilliseconds: 0);
        watcher.Observe(120, 40, 0);

        Assert.False(watcher.Observe(80, 30, 10));
        Assert.True(watcher.Observe(80, 30, 11));
    }
}

public sealed class TuiViewportTests
{
    [Theory]
    [InlineData(100, 98)]
    [InlineData(120, 118)]
    [InlineData(40, 38)]
    [InlineData(21, 20)]
    [InlineData(20, 20)]
    [InlineData(12, 12)]
    [InlineData(3, 3)]
    [InlineData(1, 1)]
    public void UsableWidth_NeverExceedsTheTerminal(int columns, int expected)
    {
        Assert.Equal(expected, TuiViewport.UsableWidth(columns));
        Assert.True(TuiViewport.UsableWidth(columns) <= columns);
    }

    [Theory]
    [InlineData(100, 10, 98)]
    [InlineData(12, 10, 10)]
    [InlineData(6, 10, 6)]
    public void UsableWidth_HonoursASmallerMinimum(int columns, int minWidth, int expected)
    {
        Assert.Equal(expected, TuiViewport.UsableWidth(columns, minWidth));
    }

    [Fact]
    public void UsableWidth_ForAnUnknownConsole_ReturnsTheMinimum()
    {
        Assert.Equal(TuiViewport.MinUsableWidth, TuiViewport.UsableWidth(0));
    }
}

public sealed class ComposerClearTests
{
    [Theory]
    [InlineData(10, 30, "\u001b[2A\r\u001b[J")]
    [InlineData(3, 30, "\u001b[2A\r\u001b[J")]
    [InlineData(2, 30, "\u001b[1A\r\u001b[J")]
    [InlineData(1, 30, "\u001b[1A\r\u001b[J")]
    public void ComposeBoxClear_ReachesPastTheFrameWhenThereIsRoom(int cursorTop, int rows, string expected)
    {
        Assert.Equal(expected, InteractiveLineReader.ComposeBoxClear(cursorTop, rows));
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void ComposeBoxClear_WithoutAViewport_WritesNothing(int cursorTop, int rows)
    {
        Assert.Equal(string.Empty, InteractiveLineReader.ComposeBoxClear(cursorTop, rows));
    }

    [Fact]
    public void ComposeBoxClear_OnATinyWindow_StillClearsTheFrame()
    {
        // A one-row window has no room for a second row of reach, but the frame still has to
        // be erasable: the terminal clamps the move to the top of the screen.
        Assert.Equal("\u001b[1A\r\u001b[J", InteractiveLineReader.ComposeBoxClear(1, 1));
    }
}
