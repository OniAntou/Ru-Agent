using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiShootingStarsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(11.99)]
    [InlineData(15)]
    [InlineData(39)]
    [InlineData(51.99)]
    [InlineData(55.1)]
    public void Long_gaps_leave_the_existing_sky_unchanged(double time)
    {
        var dots = new byte[80 * 24]; var colors = new byte[dots.Length];
        Array.Fill(dots, (byte)1); Array.Fill(colors, (byte)2);
        Assert.Equal(0, Paint(dots, colors, 80, 24, time));
        Assert.All(dots, value => Assert.Equal((byte)1, value));
        Assert.All(colors, value => Assert.Equal((byte)2, value));
    }

    [Fact]
    public void Sparse_showers_recur_and_have_moving_heads_with_fading_tails()
    {
        var dots = new byte[80 * 24]; var colors = new byte[dots.Length];
        Assert.InRange(Paint(dots, colors, 80, 24, 12.3), 1, 3);
        Assert.Contains((byte)8, colors); Assert.Contains((byte)6, colors); Assert.Contains((byte)3, colors);
        var first = dots.ToArray();
        Array.Clear(dots); Array.Clear(colors);
        Assert.InRange(Paint(dots, colors, 80, 24, 12.7), 1, 3);
        Assert.False(first.SequenceEqual(dots));
        foreach (var time in new[] { 52.8, 92.8 })
            Assert.InRange(Paint(dots, colors, 80, 24, time), 1, 3);
    }

    [Fact]
    public void Showers_are_bounded_and_do_not_allocate_per_frame()
    {
        var dots = new byte[80 * 24]; var colors = new byte[dots.Length];
        _ = Paint(dots, colors, 80, 24, 12.3);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 300; i++) Paint(dots, colors, 80, 24, i * 0.05);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Span<TuiShootingStars.Cell> cells = stackalloc TuiShootingStars.Cell[TuiShootingStars.MaxCells];
        Assert.Equal(0, TuiShootingStars.Collect(cells, int.MaxValue, 24, 12, out _));
        Assert.Equal(0, TuiShootingStars.Collect(cells, 80, 24, double.NaN, out _));
        Assert.Equal(0, TuiShootingStars.Collect([], 80, 24, 12.5, out _));
        Assert.InRange(Paint(new byte[1], new byte[1], 1, 1, 12.5), 0, 3);
    }

    [Theory]
    [InlineData(80, 24)]
    [InlineData(160, 50)]
    [InlineData(300, 140)]
    public void Head_reaches_the_last_screen_row_and_tail_exits_afterwards(int columns, int rows)
    {
        Span<TuiShootingStars.Cell> cells = stackalloc TuiShootingStars.Cell[TuiShootingStars.MaxCells];
        // First meteor, one dot before the bottom. It must remain visible beyond
        // the galaxy raster's 75% region, even on screens larger than that raster.
        var lastDotTime = 12 + 1.1 * (1 - 1d / (rows * 4 * 1.03));
        var count = TuiShootingStars.Collect(cells, columns, rows, lastDotTime, out _);
        Assert.Contains(cells[..count].ToArray(), cell => cell.Row == rows - 1 && cell.Color == 8);
        count = TuiShootingStars.Collect(cells, columns, rows, 13.16, out _);
        Assert.Contains(cells[..count].ToArray(), cell => cell.Column < columns * 0.1 && cell.Row >= rows * 0.8);
        count = TuiShootingStars.Collect(cells, columns, rows, 13.5, out _);
        Assert.DoesNotContain(cells[..count].ToArray(), cell => cell.Column < columns * 0.1);
    }

    [Fact]
    public void Extended_tail_spans_more_than_a_quarter_of_the_screen()
    {
        Span<TuiShootingStars.Cell> cells = stackalloc TuiShootingStars.Cell[TuiShootingStars.MaxCells];
        var count = TuiShootingStars.Collect(cells, 160, 50, 12.6, out _);
        var meteor = cells[..count].ToArray();
        Assert.NotEmpty(meteor);
        Assert.InRange(meteor.Max(cell => cell.Row) - meteor.Min(cell => cell.Row), 13, 16);
        // The third meteor must not be cut off by the old three-second shower limit.
        count = TuiShootingStars.Collect(cells, 160, 50, 55.02, out _);
        Assert.NotEmpty(cells[..count].ToArray());
    }

    [Fact]
    public void Sparse_cells_are_unique_bounded_and_follow_resizes()
    {
        Span<TuiShootingStars.Cell> cells = stackalloc TuiShootingStars.Cell[TuiShootingStars.MaxCells];
        foreach (var (columns, rows) in new[] { (160, 50), (35, 12), (1, 1), (300, 140) })
        for (var frame = 0; frame < 60; frame++)
        {
            var count = TuiShootingStars.Collect(cells, columns, rows, 52 + frame * 0.05, out _);
            var positions = new HashSet<(int, int)>();
            foreach (var cell in cells[..count])
            {
                Assert.InRange(cell.Column, 0, columns - 1); Assert.InRange(cell.Row, 0, rows - 1);
                Assert.NotEqual((byte)0, cell.Dots);
                Assert.True(positions.Add((cell.Column, cell.Row)));
            }
        }
    }

    private static int Paint(byte[] dots, byte[] colors, int columns, int rows, double time)
    {
        Span<TuiShootingStars.Cell> cells = stackalloc TuiShootingStars.Cell[TuiShootingStars.MaxCells];
        var count = TuiShootingStars.Collect(cells, columns, rows, time, out var active);
        foreach (var cell in cells[..count])
        {
            var index = cell.Row * columns + cell.Column;
            dots[index] |= cell.Dots;
            colors[index] = Math.Max(colors[index], cell.Color);
        }
        return active;
    }
}
