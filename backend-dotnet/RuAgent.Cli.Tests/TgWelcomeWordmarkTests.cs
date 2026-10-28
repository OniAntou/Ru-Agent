using RuAgent.Cli.TerminalGui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgWelcomeWordmarkTests
{
    [Fact]
    public void Sweep_moves_across_the_logo_repeats_and_leaves_long_quiet_intervals()
    {
        var quiet = TgWelcomeWordmark.SweepPosition(0);
        Assert.Equal(TgPalette.ToColor((112, 225, 187)), TgWelcomeWordmark.AttributeAt(1, 0, quiet).Foreground);
        Assert.Equal(TgPalette.ToColor((101, 191, 222)), TgWelcomeWordmark.AttributeAt(1, 8, quiet).Foreground);
        Assert.Equal(TgPalette.ToColor((215, 232, 236)), TgWelcomeWordmark.AttributeAt(1, 10, quiet).Foreground);
        foreach (var time in new[] { 0d, 1.99, 4.5, 6, 9.99 })
            Assert.True(double.IsNaN(TgWelcomeWordmark.SweepPosition(time)));
        var first = TgWelcomeWordmark.SweepPosition(3);
        var later = TgWelcomeWordmark.SweepPosition(4);
        Assert.True(first < later);
        Assert.NotEqual(TgWelcomeWordmark.AttributeAt(0, 12, quiet), TgWelcomeWordmark.AttributeAt(0, 12, first));
        Assert.Equal(TgWelcomeWordmark.AttributeAt(0, 12, quiet), TgWelcomeWordmark.AttributeAt(0, 12, later));
        Assert.Equal(TgWelcomeWordmark.AttributeAt(0, 31, quiet), TgWelcomeWordmark.AttributeAt(0, 31, first));
        Assert.NotEqual(TgWelcomeWordmark.AttributeAt(0, 31, quiet), TgWelcomeWordmark.AttributeAt(0, 31, later));
        Assert.Equal(first, TgWelcomeWordmark.SweepPosition(13), 8);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void Invalid_clock_values_leave_the_logo_at_its_quiet_colors(double time)
    {
        Assert.True(double.IsNaN(TgWelcomeWordmark.SweepPosition(time)));
        Assert.Equal(TgWelcomeWordmark.AttributeAt(0, 0, double.NaN),
            TgWelcomeWordmark.AttributeAt(0, 0, TgWelcomeWordmark.SweepPosition(time)));
    }

    [Fact]
    public void Cached_frame_lookups_allocate_no_managed_memory()
    {
        var lastAttribute = TgWelcomeWordmark.AttributeAt(0, 0, double.NaN);
        var lastGlyph = TgWelcomeWordmark.Glyph(0, 0);
        _ = TgWelcomeWordmark.ShadowGlyph(0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 300; frame++)
        {
            var beam = TgWelcomeWordmark.SweepPosition(frame * 0.05);
            for (var row = 0; row < 3; row++)
            for (var column = 0; column < TgWelcomeWordmark.Width; column++)
            {
                lastAttribute = TgWelcomeWordmark.AttributeAt(row, column, beam);
                lastGlyph = TgWelcomeWordmark.Glyph(row, column);
                _ = TgWelcomeWordmark.ShadowGlyph(column);
            }
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.NotNull(lastGlyph);
        Assert.Equal(TgPalette.ToColor((0, 0, 0)), lastAttribute.Background);
    }
}
