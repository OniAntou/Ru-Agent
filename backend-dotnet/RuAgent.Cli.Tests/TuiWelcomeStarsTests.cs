using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiWelcomeStarsTests
{
    [Fact]
    public void Stars_are_sparse_deterministic_and_keep_their_positions_while_twinkling()
    {
        var occupied = 0; var changed = 0;
        var tones = new HashSet<byte>();
        for (var y = 30; y < 50; y++)
        for (var x = 0; x < 160; x++)
        {
            var first = TuiWelcomeStars.At(x, y, 0);
            Assert.Equal(first, TuiWelcomeStars.At(x, y, 0));
            var later = TuiWelcomeStars.At(x, y, 4);
            Assert.Equal(first.Dots, later.Dots);
            if (first.Dots == 0) Assert.Equal((byte)0, first.Color);
            else { occupied++; tones.Add(first.Color); Assert.Contains(first.Color, new byte[] { 2, 3, 4, 5, 6 }); }
            if (first.Color != later.Color) changed++;
        }
        Assert.InRange(occupied, 60, 160);
        Assert.True(changed > 0);
        foreach (var tone in new byte[] { 4, 5, 6 }) Assert.Contains(tone, tones);
    }

    [Fact]
    public void Repeated_star_sampling_allocates_no_managed_memory()
    {
        _ = TuiWelcomeStars.At(0, 0, 0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) _ = TuiWelcomeStars.At(i % 160, i / 160, 3);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(2, -1)]
    public void Invalid_coordinates_have_no_star(int x, int y) =>
        Assert.Equal(((byte)0, (byte)0), TuiWelcomeStars.At(x, y, double.NaN));
}
