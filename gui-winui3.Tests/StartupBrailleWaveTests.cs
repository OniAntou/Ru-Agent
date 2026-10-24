using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class StartupBrailleWaveTests
{
    [Theory]
    [InlineData(96, 36)]
    [InlineData(30, 20)]
    public void Frames_are_braille_with_a_clear_center_and_stable_dimensions(int columns, int rows)
    {
        var frame = StartupBrailleWave.Frame(columns, rows, 0);
        var lines = frame.Split('\n');
        Assert.Equal(rows, lines.Length);
        Assert.All(lines, line => Assert.Equal(columns, line.Length));
        Assert.All(frame.Where(c => c != '\n'), c => Assert.InRange(c, '\u2800', '\u28ff'));
        Assert.Equal('\u2800', lines[rows / 2][columns / 2]);
        Assert.Contains(frame, c => c > '\u2800');
        Assert.Equal(frame, StartupBrailleWave.Frame(columns, rows, 0));
        Assert.NotEqual(frame, StartupBrailleWave.Frame(columns, rows, 3));
    }

    [Fact]
    public void Adjacent_frames_keep_the_dot_field_stable_instead_of_randomly_flickering()
    {
        var first = StartupBrailleWave.Frame(96, 36, 1);
        var next = StartupBrailleWave.Frame(96, 36, 1.05);
        int changedDots = first.Zip(next).Sum(pair =>
            System.Numerics.BitOperations.PopCount((uint)(pair.First ^ pair.Second)));
        Assert.InRange(changedDots, 1, 96 * 36 * 8 / 20);
    }

    [Fact]
    public void Invalid_or_oversized_dimensions_are_bounded()
    {
        Assert.Single(StartupBrailleWave.Frame(-1, 0, double.NaN));
        var lines = StartupBrailleWave.Frame(int.MaxValue, int.MaxValue, double.PositiveInfinity).Split('\n');
        Assert.Equal(56, lines.Length);
        Assert.All(lines, line => Assert.Equal(128, line.Length));
    }
}
