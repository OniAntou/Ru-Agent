using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiHeaderTests
{
    [Theory]
    [InlineData(@"D:\LLM", 40, "D:/LLM")]
    [InlineData(@"D:\a\bbbbbbbb\cccccccc", 12, "…/cccccccc")]
    public void ShortenWorkspace_KeepsTheLeafVisible(string workspace, int maxCells, string expected)
    {
        Assert.Equal(expected, TuiHeader.ShortenWorkspace(workspace, maxCells));
    }

    [Fact]
    public void ShortenWorkspace_TruncatesWhenASingleSegmentIsTooLong()
    {
        var shortened = TuiHeader.ShortenWorkspace("/very/long/single-segment-name-here", 10);

        Assert.True(TuiText.VisibleWidth(shortened) <= 10);
    }

    [Fact]
    public void Wordmark_IsThreeRowsAndEveryLetterStillHasABase()
    {
        Assert.Equal(3, TuiHeader.WordmarkRows);
        Assert.Equal(TuiHeader.WordmarkRows, TuiHeader.WordmarkRuRows.Count);
        Assert.Equal(TuiHeader.WordmarkRows, TuiHeader.WordmarkAgentRows.Count);

        // U and G lose their bottom bar with the feet row, so they close inside the last row
        // instead of ending in two open stems. U keeps its two side cells full height so the
        // stems meet the bowl: with only the bowl drawn there the letter broke on both sides.
        Assert.Equal("█  █ █▄▄█", TuiHeader.WordmarkRuRows[2]);

        // G carries the crossbar on the lower half of its middle row and closes only its two
        // feet. A crossbar up top with a solid base read as a barred C; a filled base alone
        // read as an O.
        Assert.Contains("█ ▄▄", TuiHeader.WordmarkAgentRows[1], StringComparison.Ordinal);
        Assert.Contains("█▄▄█", TuiHeader.WordmarkAgentRows[2], StringComparison.Ordinal);

        // E rides each bar on the inner edge of its row: the base sits on the lower half. Without
        // it the letter read as a bare stem with one arm.
        Assert.Contains("█▀▀ ", TuiHeader.WordmarkAgentRows[1], StringComparison.Ordinal);
        Assert.Contains("█▄▄▄", TuiHeader.WordmarkAgentRows[2], StringComparison.Ordinal);

        // Every letter spans the same height: nothing may end a half-cell short of the rest.
        Assert.Equal([6, 6, 6, 6, 6, 6, 6], WordmarkHeights());
    }

    /// <summary>Visual height of each letter in half-cells, from the first to the last ink.</summary>
    private static List<int> WordmarkHeights()
    {
        var letters = new[] { (0, 3), (5, 8), (10, 13), (15, 18), (20, 23), (25, 28), (30, 32) };
        var heights = new List<int>();
        foreach (var (from, to) in letters)
        {
            var top = int.MaxValue;
            var bottom = -1;
            for (var row = 0; row < TuiHeader.WordmarkRows; row++)
            {
                var line = TuiHeader.WordmarkRuRows[row] + " " + TuiHeader.WordmarkAgentRows[row];
                for (var col = from; col <= to; col++)
                {
                    for (var half = 0; half < 2; half++)
                    {
                        // A full block covers both halves; a half block covers one.
                        var covers = half == 0 ? line[col] != ' ' : line[col] is '█' or '▄';
                        if (!covers)
                        {
                            continue;
                        }

                        var at = (row * 2) + half;
                        top = Math.Min(top, at);
                        bottom = Math.Max(bottom, at);
                    }
                }
            }

            heights.Add(bottom - top + 1);
        }

        return heights;
    }

}
