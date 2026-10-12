using System.Text;
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiHeaderTests : IDisposable
{
    private readonly IDisposable _levelPin;

    public TuiHeaderTests()
    {
        _levelPin = TuiPalette.PinLevel(TuiColorLevel.None);
    }

    public void Dispose() => _levelPin.Dispose();

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(200)]
    public void Compose_HeaderLineFitsTheViewport(int width)
    {
        var line = TuiHeader.Compose(width, "Homepage button color change in repo workflow", 39413);

        Assert.True(TuiText.VisibleWidth(line) <= width, $"line exceeds {width}: '{line}'");
    }

    [Fact]
    public void Compose_KeepsExactWidthTitleAndGroupedTally()
    {
        var line = TuiHeader.Compose(100, "Fix broken tests", 39413);

        Assert.Equal(100, TuiText.VisibleWidth(line));
        Assert.StartsWith("# Fix broken tests", line, StringComparison.Ordinal);
        Assert.EndsWith("39,413", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_SingleRowCaptionsTheSessionWithoutTitle()
    {
        var line = TuiHeader.Compose(40, null, null);

        Assert.Equal(40, TuiText.VisibleWidth(line));
        Assert.StartsWith("# session", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_DropsTheTallyWhenTheRowCannotFitBoth()
    {
        var line = TuiHeader.Compose(12, "A long session title", 1234567);

        Assert.Equal(12, TuiText.VisibleWidth(line));
        Assert.DoesNotContain("1,234,567", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ShowsContextPercentAndCostInTheCluster()
    {
        var line = TuiHeader.Compose(100, "Fix broken tests", 39413, contextPercent: 20, costUsd: 0.29);

        Assert.Contains("39,413", line, StringComparison.Ordinal);
        Assert.Contains("20%", line, StringComparison.Ordinal);
        Assert.Contains("($0.29)", line, StringComparison.Ordinal);
        Assert.Equal(100, TuiText.VisibleWidth(line));
    }

    [Fact]
    public void Compose_OmitsUnpublishedClusterMembers()
    {
        var line = TuiHeader.Compose(60, "Fix", null, contextPercent: 20);

        Assert.Contains("20%", line, StringComparison.Ordinal);
        Assert.DoesNotContain("($", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_ZeroWidthReturnsNothing()
    {
        Assert.Empty(TuiHeader.Compose(0, "title", 1));
    }

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

    [Theory]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(120)]
    public void ComposeLandingScreen_FitsTheViewportWithTheWordmark(int width)
    {
        var lines = TuiHeader.ComposeLandingScreen(width);

        var text = string.Join("\n", lines);
        Assert.Contains("█", text, StringComparison.Ordinal);
        Assert.All(lines, l => Assert.True(TuiText.VisibleWidth(l) <= width));
    }

    /// <summary>
    /// The wordmark is three block rows. It used to be four, the last one a strip of feet drawn as
    /// top-half blocks, which left every letter seven half-cells tall with an empty band beneath it.
    /// </summary>
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

    [Fact]
    public void Wordmark_ShadesDiagonalLeftBright()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            var lines = TuiHeader.Wordmark(80);

            var joined = string.Join("\n", lines);
            Assert.Contains(TuiPalette.Code(TuiToken.Aurora), joined, StringComparison.Ordinal);
            Assert.Contains(TuiPalette.Code(TuiToken.AuroraDim), joined, StringComparison.Ordinal);
            Assert.Contains(TuiPalette.Code(TuiToken.Bright), joined, StringComparison.Ordinal);
            Assert.Contains(TuiPalette.Code(TuiToken.Meta), joined, StringComparison.Ordinal);
            Assert.DoesNotContain(TuiPalette.Code(TuiToken.Line), joined, StringComparison.Ordinal);
            Assert.Contains("█", TuiText.Strip(joined), StringComparison.Ordinal);
        }

        var topRow = TuiHeader.WordmarkRowCells(0);
        Assert.Equal(TuiToken.Aurora, FirstPainted(topRow));
        var bottomRow = TuiHeader.WordmarkRowCells(TuiHeader.WordmarkRows - 1);
        Assert.Equal(TuiToken.Aurora, FirstPainted(bottomRow));
        Assert.Equal(TuiToken.Meta, LastPainted(bottomRow));

        static TuiToken? FirstPainted(IReadOnlyList<(char Glyph, TuiToken? Token)> cells) =>
            cells.First(cell => cell.Glyph != ' ').Token;
        static TuiToken? LastPainted(IReadOnlyList<(char Glyph, TuiToken? Token)> cells) =>
            cells.Last(cell => cell.Glyph != ' ').Token;
    }

    [Fact]
    public void Wordmark_CentersTheBlockAndKeepsRowCount()
    {
        var lines = TuiHeader.Wordmark(80);

        Assert.Equal(TuiHeader.WordmarkRows + 2, lines.Count);
        Assert.Equal(string.Empty, TuiText.Strip(lines[0]));
        Assert.Equal(string.Empty, TuiText.Strip(lines[^1]));
        Assert.True(TuiText.Strip(lines[1]).Trim().Length > 0);
        Assert.True(TuiText.Strip(lines[^2]).Trim().Length > 0);
        Assert.StartsWith(" ", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Wordmark_FallsBackToTextWhenNarrow()
    {
        var lines = TuiHeader.Wordmark(30);

        var text = string.Join("\n", lines);
        Assert.Contains("Ru Agent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("█", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Starfield_IsDeterministicAndSparse()
    {
        Assert.Equal(TuiHeader.StarCell(10, 5), TuiHeader.StarCell(10, 5));

        var stars = 0;
        for (var row = 0; row < 50; row++)
        {
            for (var col = 0; col < 160; col++)
            {
                if (TuiHeader.StarCell(col, row) is not null)
                {
                    stars++;
                }
            }
        }

        Assert.InRange(stars, 10, 120);
    }

    [Fact]
    public void Starfield_AnewSeedMovesTheStarsSomewhereElse()
    {
        const int Width = 160;
        const int Height = 50;
        var sky = new List<string>();
        for (var seed = 0; seed < 4; seed++)
        {
            var view = new StringBuilder();
            for (var row = 0; row < Height; row++)
            {
                for (var col = 0; col < Width; col++)
                {
                    view.Append(TuiHeader.StarCell(col, row, seed) is null ? ' ' : '*');
                }
            }

            sky.Add(view.ToString());
        }

        // Reseeding has to actually change the sky, or the stars never look random.
        Assert.Equal(4, sky.Distinct().Count());

        // Every seed stays sparse, otherwise a scattered sky becomes a busy one.
        foreach (var view in sky)
        {
            Assert.InRange(view.Count(c => c == '*'), 10, 120);
        }
    }

    [Fact]
    public void Meteor_FallsAsDotClumpThenPausesAndLoops()
    {
        var first = TuiHeader.MeteorCells(0, 80, 24);
        Assert.All(first, cell => Assert.InRange(cell.Glyph, '⠀', '⣿'));
        Assert.Contains(first, cell => cell.Token == TuiToken.Bright);
        Assert.Contains(TuiHeader.MeteorCells(8, 80, 24), cell => cell.Token == TuiToken.Meta);

        Assert.Equal(first, TuiHeader.MeteorCells(TuiHeader.MeteorCooldownTicks, 80, 24));
    }

    [Fact]
    public void Meteor_EachTakesATurnAndThenWaitsItsCooldown()
    {
        // They fly one at a time, each waiting its own cooldown before the next turn.
        var takeoffs = new List<(int Col, int Row)>();
        var together = 0;
        for (var tick = 0; tick < TuiHeader.MeteorCooldownTicks; tick++)
        {
            var flying = Enumerable.Range(0, TuiHeader.MeteorCount)
                .Count(meteor => TuiHeader.MeteorHead(meteor, tick, 80, 24) is not null);
            if (flying > 1)
            {
                together++;
            }

            foreach (var meteor in Enumerable.Range(0, TuiHeader.MeteorCount))
            {
                if (TuiHeader.MeteorHead(meteor, tick, 80, 24) is { } head)
                {
                    takeoffs.Add((head.Col, head.Row));
                }
            }
        }

        Assert.Equal(0, together);
        Assert.True(takeoffs.Distinct().Count() > TuiHeader.MeteorCount);

        // A meteor repeats on its own cooldown, not on a shared short one.
        var period = TuiHeader.MeteorCooldownSeconds * 1000 / TuiHeader.MeteorFrameMilliseconds;
        Assert.Equal(period, TuiHeader.MeteorCooldownTicks);
        Assert.Equal(14, TuiHeader.MeteorCooldownSeconds);
        Assert.True(
            TuiHeader.MeteorCooldownTicks > TuiHeader.MeteorTravelTicks * 10,
            "the meteors would come round too often to read as a cooldown");
    }

    [Fact]
    public void Meteor_TheTurnsAreSpreadEvenlyAroundTheCooldown()
    {
        // Consecutive take-offs are a third of the cooldown apart, so the sky always has one and
        // only one meteor in it rather than a clump of arrivals.
        var starts = new List<int>();
        foreach (var meteor in Enumerable.Range(0, TuiHeader.MeteorCount))
        {
            for (var tick = 0; tick < TuiHeader.MeteorCooldownTicks; tick++)
            {
                if (TuiHeader.MeteorHead(meteor, tick, 80, 24) is not null)
                {
                    starts.Add(tick);
                    break;
                }
            }
        }

        starts.Sort();
        Assert.Equal(TuiHeader.MeteorCount, starts.Count);

        // Take-off offsets wrap the cooldown, so the turn that crosses zero closes the loop.
        var gaps = starts.Zip(starts.Skip(1).Append(TuiHeader.MeteorCooldownTicks), (a, b) => b - a).ToList();
        Assert.All(gaps, gap => Assert.InRange(gap, TuiHeader.MeteorTravelTicks, TuiHeader.MeteorCooldownTicks));

        // The turns add up to exactly one cooldown, so no one hogs the sky.
        Assert.Equal(TuiHeader.MeteorCooldownTicks, gaps.Sum());
    }

    [Fact]
    public void Meteor_EachStreakSurvivesAnotherMeteorCrossingIt()
    {
        // Two meteors may paint the same cell; what must not happen is one of them deleting the
        // other's specks, so every streak stays whole at its own head whatever else is in the sky.
        foreach (var tick in new[] { 4, 8, 12, 16, 20 })
        {
            for (var meteor = 0; meteor < TuiHeader.MeteorCount; meteor++)
            {
                var streak = StreakOf(meteor, tick);
                if (streak.Count == 0)
                {
                    continue;
                }

                var head = TuiHeader.MeteorHead(meteor, tick, 80, 24);
                Assert.NotNull(head);
                var onScreen = head!.Value.Row < 24 && head.Value.Row >= 0
                    && head.Value.Col >= 0 && head.Value.Col < 80;

                // The head keeps the leading tone and its densest speck for as long as it is on
                // screen. Past the bottom edge what is left is still the bright end of the tail,
                // because the leading tone spans a few cells, not one.
                if (onScreen)
                {
                    Assert.Equal(TuiToken.Bright, streak[0].Token);
                    Assert.Equal(SpeckDenseDots, BrailleDots(streak[0].Glyph));
                }

                Assert.True(
                    streak.All(cell => cell.Row < 24 && cell.Row >= 0 && cell.Col >= 0 && cell.Col < 80),
                    $"meteor {meteor} painted outside the viewport at tick {tick}");
            }
        }
    }

    [Fact]
    public void Meteor_OneAtATimeWithRealGapsBetween()
    {
        var perTick = new List<int>();
        for (var tick = 0; tick < TuiHeader.MeteorCooldownTicks; tick++)
        {
            perTick.Add(Enumerable.Range(0, TuiHeader.MeteorCount)
                .Count(meteor => TuiHeader.MeteorHead(meteor, tick, 80, 24) is not null));
        }

        // Never a pile-up: the meteors take turns, one in the air at a time.
        Assert.Equal(0, perTick.Count(count => count > 1));

        // Each one flies its whole flight before the next turns, so the busy ticks are the
        // meteors' flights back to back rather than one long streak.
        Assert.Equal(TuiHeader.MeteorCount * TuiHeader.MeteorTravelTicks, perTick.Count(count => count == 1));

        // And the empty stretches between flights are long, not a flicker.
        var emptyRuns = new List<int>();
        var run = 0;
        foreach (var count in perTick)
        {
            run = count == 0 ? run + 1 : 0;
            if (run > 0)
            {
                emptyRuns.Add(run);
            }
        }

        // Longest stretch with nobody in the sky. Turns are an equal share of the cooldown apart,
        // so the empty run covers that share less the one flight it interrupts.
        var longest = emptyRuns.Max();
        var share = TuiHeader.MeteorCooldownTicks / TuiHeader.MeteorCount;
        Assert.True(
            longest >= share - TuiHeader.MeteorTravelTicks,
            $"the gap between meteors is too short to read as a cooldown: {longest} ticks, want {share - TuiHeader.MeteorTravelTicks}");
    }

    [Fact]
    public void Meteor_AcceleratesFromADriftIntoADive()
    {
        var steps = new List<int>();
        var lastIndex = -1;
        for (var tick = 0; tick < TuiHeader.MeteorTravelTicks; tick++)
        {
            var head = TuiHeader.MeteorHead(0, tick, 80, 24);
            Assert.NotNull(head);
            steps.Add(head.Value.Index - lastIndex);
            lastIndex = head.Value.Index;
        }

        var early = steps.Take(steps.Count / 3).Sum();
        var late = steps.Skip(steps.Count * 2 / 3).Sum();

        // Slow off the mark, then a clear dive: the tail of the flight covers far more path.
        Assert.True(late > early, $"late travel {late} did not outrun early travel {early}");
        Assert.Contains(0, steps.Take(steps.Count / 3));
        Assert.Contains(steps[^1], steps.Skip(steps.Count * 2 / 3));
    }

    [Fact]
    public void Meteor_HeadAndTrailStayAdjacent()
    {
        foreach (var tick in new[] { 4, 8, 12, 16 })
        {
            var painted = TuiHeader.MeteorCells(tick, 80, 24).Select(cell => (cell.Col, cell.Row)).ToHashSet();
            Assert.NotEmpty(painted);

            // Each streak is contiguous on its own. Several meteors can leave a real gap between
            // them, so the neighbour check runs per streak: the cells of one meteor, gathered by
            // walking its own path rather than over everything painted this tick.
            foreach (var meteor in Enumerable.Range(0, TuiHeader.MeteorCount))
            {
                var streak = StreakOf(meteor, tick);
                if (streak.Count < 2)
                {
                    continue;
                }

                foreach (var cell in streak)
                {
                    var offsets = Enumerable.Range(-1, 3)
                        .SelectMany(dx => Enumerable.Range(-1, 3).Select(dy => (Dx: dx, Dy: dy)))
                        .Where(offset => offset.Dx != 0 || offset.Dy != 0)
                        .Select(offset => (cell.Col + offset.Dx, cell.Row + offset.Dy));
                    Assert.Contains(offsets, painted.Contains);
                }
            }
        }
    }

    /// <summary>Meteor one's own streak, which is what the contiguity and head checks read.</summary>
    private static IReadOnlyList<(int Col, int Row, char Glyph, TuiToken Token)> StreakOf(
        int meteor,
        int tick)
    {
        return TuiHeader.MeteorStreak(meteor, tick, 80, 24);
    }

    [Fact]
    public void Meteor_FallsAtFortyFiveDegreesFromTheTopRightToTheBottomLeft()
    {
        Assert.NotEmpty(TuiHeader.MeteorCells(0, 80, 24));

        // Every meteor falls along a straight forty-five degree line: one column left and one
        // row down per step. Checked per streak, because several meteors overlap in the sky.
        foreach (var meteor in Enumerable.Range(0, TuiHeader.MeteorCount))
        {
            var head = TuiHeader.MeteorHead(meteor, TuiHeader.MeteorTravelTicks / 2, 80, 24);
            if (head is not { } leading)
            {
                continue;
            }

            Assert.InRange(leading.Row, 0, 23);
            Assert.InRange(leading.Col, 0, 79);

            var streak = StreakOf(meteor, TuiHeader.MeteorTravelTicks / 2);
            Assert.True(streak.Count > 1);
            for (var i = 1; i < streak.Count; i++)
            {
                // Head first, so each step back up the trail moves one column right and one row up.
                // Head first: each step back up the trail moves one column right and one row up.
                Assert.Equal(-1, streak[i - 1].Col - streak[i].Col);
                Assert.Equal(-1, streak[i].Row - streak[i - 1].Row);
            }
        }
    }

    [Fact]
    public void Meteor_SpeckThinsFromTheHeadBackToTheTail()
    {
        // Read one meteor's own streak, head first, mid-flight where it is fully on screen.
        var flight = TuiHeader.MeteorTravelTicks / 2;
        var streak = StreakOf(0, flight).ToList();
        Assert.True(streak.Count > 4);

        Assert.Equal(SpeckDenseDots, BrailleDots(streak[0].Glyph));
        for (var i = 1; i < streak.Count; i++)
        {
            Assert.True(
                BrailleDots(streak[i - 1].Glyph) >= BrailleDots(streak[i].Glyph),
                $"speck {i} along the tail thickened instead of thinning");
        }

        Assert.Equal(SpeckFaintDots, streak.Min(cell => BrailleDots(cell.Glyph)));
        Assert.Equal(SpeckDenseDots, streak.Max(cell => BrailleDots(cell.Glyph)));
    }

    [Fact]
    public void Meteor_LightsOnlyBehindTheHeadSoTheStreakStaysWhole()
    {
        // Mid-flight of the group, where every meteor is on screen with its tail out.
        foreach (var tick in Enumerable.Range(0, TuiHeader.MeteorTravelTicks))
        {
            foreach (var meteor in Enumerable.Range(0, TuiHeader.MeteorCount))
            {
                var streak = StreakOf(meteor, tick);
                var head = TuiHeader.MeteorHead(meteor, tick, 80, 24);
                if (streak.Count == 0 || head is not { } leading)
                {
                    continue;
                }

                var onScreen = leading.Row is >= 0 and < 24 && leading.Col is >= 0 and < 80;
                var lowest = streak.MaxBy(cell => cell.Row)!;
                if (onScreen)
                {
                    Assert.Equal(TuiToken.Bright, lowest.Token);
                }

                // The tail trails up and to the right of the head; nothing leads it.
                foreach (var cell in streak.Where(cell => cell != lowest))
                {
                    Assert.True(cell.Row < lowest.Row || cell.Col > lowest.Col);
                }

                // While the head itself is still on screen it is the lowest cell, and densest.
                if (onScreen)
                {
                    Assert.Equal(leading.Row, lowest.Row);
                    Assert.Equal(SpeckDenseDots, BrailleDots(lowest.Glyph));
                }
            }
        }
    }

    [Fact]
    public void Meteor_KeepsFlyingUntilTheHeadItselfLeavesTheViewport()
    {
        // Every speck keeps its own tone all the way down: nothing dims on the way out, the
        // streak is only eaten by the edge.
        var midFlight = TuiHeader.MeteorTravelTicks / 2;
        foreach (var tick in new[] { midFlight / 2, midFlight })
        {
            var streak = StreakOf(0, tick);
            if (streak.Count == 0)
            {
                continue;
            }

            Assert.Equal(SpeckDenseDots, BrailleDots(streak[0].Glyph));
            Assert.DoesNotContain(streak, cell => cell.Token == TuiToken.Line);
        }

        // Past the last row the streak is eaten by the edge one cell at a time, and the last
        // flight tick has cleared the screen entirely because the tail got runway to leave too.
        var counts = new List<int>();
        for (var tick = TuiHeader.MeteorTravelTicks / 2; tick < TuiHeader.MeteorTravelTicks; tick++)
        {
            counts.Add(StreakOf(0, tick).Count);
        }

        // Two phases: the streak grows as the meteor gathers speed and its tail sweeps out, then the
        // bottom edge eats it one cell at a time until the last flight tick has cleared the sky.
        Assert.True(counts[0] >= 1);
        Assert.Equal(0, counts[^1]);
        var peak = counts.IndexOf(counts.Max());
        for (var i = 1; i <= peak; i++)
        {
            if (counts[i] < counts[i - 1])
            {
                Assert.Fail($"the streak shrank before its peak at tick {i}");
            }
        }

        for (var i = peak + 1; i < counts.Count; i++)
        {
            if (counts[i] > counts[i - 1])
            {
                Assert.Fail($"the streak grew back after its peak at tick {i}");
            }
        }
    }

    private const int SpeckDenseDots = 4;

    private const int SpeckMediumDots = 3;

    private const int SpeckFaintDots = 2;

    private static int BrailleDots(char glyph) => System.Numerics.BitOperations.PopCount((uint)(glyph - '⠀'));

    /// <summary>dot4 is the top-right corner of the cell, dot7 the bottom-left one.</summary>
    private const int DiagonalTopRight = 0b0000_1000;

    private const int DiagonalBottomLeft = 0b0100_0000;

    [Fact]
    public void Meteor_EverySpeckLightsBothEndsOfTheDiagonalItFallsAlong()
    {
        // The streak runs down-left at forty-five degrees, so it enters a cell at the top-right
        // corner and leaves at the bottom-left one. A mask missing either end breaks the streak
        // there: the tail stops short of the clump, or the clump never joins the tail.
        var diagonal = DiagonalTopRight | DiagonalBottomLeft;

        foreach (var tick in new[] { 0, 11, 22, 33 })
        {
            foreach (var (_, _, glyph, _) in TuiHeader.MeteorCells(tick, 80, 24))
            {
                Assert.Equal(diagonal, (glyph - '⠀') & diagonal);
            }
        }
    }

    [Fact]
    public void Meteor_StaysInsideTheBounds()
    {
        foreach (var tick in new[] { 0, 10, 33, 34 })
        {
            foreach (var (col, row, _, _) in TuiHeader.MeteorCells(tick, 80, 24))
            {
                Assert.InRange(col, 0, 79);
                Assert.InRange(row, 0, 23);
            }
        }

        foreach (var (width, height) in new[] { (40, 8), (80, 24), (200, 60) })
        {
            foreach (var tick in new[] { 0, 17, 33 })
            {
                foreach (var (col, row, _, _) in TuiHeader.MeteorCells(tick, width, height))
                {
                    Assert.InRange(col, 0, width - 1);
                    Assert.InRange(row, 0, height - 1);
                }
            }
        }
    }

    [Fact]
    public void ComposeLandingScreen_PadsWithStarlight()
    {
        var lines = TuiHeader.ComposeLandingScreen(80, viewportRows: 60);

        var text = string.Join("\n", lines);
        Assert.Contains("✦", TuiText.Strip(text), StringComparison.Ordinal);
        Assert.All(lines, l => Assert.True(TuiText.VisibleWidth(l) <= 80));
    }

    [Fact]
    public void ComposeLandingScreen_CentersVerticallyWithRoomToSpare()
    {
        var flat = TuiHeader.ComposeLandingScreen(80);
        var centered = TuiHeader.ComposeLandingScreen(80, viewportRows: 60);

        Assert.True(centered.Count > flat.Count);
        Assert.All(centered, l => Assert.True(TuiText.VisibleWidth(l) <= 80));
    }

    [Fact]
    public void ComposeLandingScreen_ZeroWidthReturnsNothing()
    {
        Assert.Empty(TuiHeader.ComposeLandingScreen(0));
    }
}
