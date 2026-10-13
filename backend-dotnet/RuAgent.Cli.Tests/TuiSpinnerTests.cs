using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// The shared animation clock: every phase derives from elapsed seconds, so the
/// 18ms meteor ticker and a 100ms ticker produce identical motion.
/// </summary>
public sealed class TuiSpinnerTests
{
    [Fact]
    public void FrameAt_AdvancesWithTimeNotCallCount()
    {
        Assert.Equal("⠋", TuiSpinner.FrameAt(0));
        Assert.Equal("⠋", TuiSpinner.FrameAt(0.09));
        Assert.Equal("⠙", TuiSpinner.FrameAt(0.1));
        Assert.Equal("⠹", TuiSpinner.FrameAt(0.25));

        // One full rotation takes one second of elapsed time, whatever the tick rate.
        Assert.Equal("⠋", TuiSpinner.FrameAt(1.0));
        Assert.Equal("⠋", TuiSpinner.FrameAt(1.05));
        Assert.Equal("⠏", TuiSpinner.FrameAt(0.95));
    }

    [Fact]
    public void FrameAt_ClampsNegativeElapsedToTheFirstFrame()
    {
        Assert.Equal("⠋", TuiSpinner.FrameAt(-1));
    }

    [Fact]
    public void ToolGlyphAt_RotatesTheHalfCircle()
    {
        Assert.Equal("◐", TuiSpinner.ToolGlyphAt(0));
        Assert.Equal("◓", TuiSpinner.ToolGlyphAt(0.25));
        Assert.Equal("◑", TuiSpinner.ToolGlyphAt(0.5));
        Assert.Equal("◒", TuiSpinner.ToolGlyphAt(0.75));
        Assert.Equal("◐", TuiSpinner.ToolGlyphAt(1.0));
    }

    [Fact]
    public void Frames_KeepTheBrailleVocabulary()
    {
        Assert.Equal(10, TuiSpinner.Frames.Count);
        Assert.Equal(4, TuiSpinner.ToolGlyphs.Count);
        Assert.All(TuiSpinner.Frames, frame => Assert.Equal(1, TuiText.VisibleWidth(frame)));
        Assert.All(TuiSpinner.ToolGlyphs, glyph => Assert.Equal(1, TuiText.VisibleWidth(glyph)));
    }

    [Fact]
    public void WaveBucket_AdvancesWithTimeAndClampsNegativeElapsed()
    {
        Assert.Equal(0, TuiSpinner.WaveBucketAt(0));
        Assert.Equal(0, TuiSpinner.WaveBucketAt(-1));
        Assert.Equal(0, TuiSpinner.WaveBucketAt(0.089));
        Assert.Equal(1, TuiSpinner.WaveBucketAt(0.09));
        Assert.Equal(11, TuiSpinner.WaveBucketAt(1.0));
    }

    [Fact]
    public void WaveRgb_IsDeterministicAndKeepsTheDotsDistinct()
    {
        // Three cells, three ramp positions: the row reads as a wave, not one tone.
        var first = TuiSpinner.WaveRgbAt(0, 0.35);
        var second = TuiSpinner.WaveRgbAt(1, 0.35);
        var third = TuiSpinner.WaveRgbAt(2, 0.35);

        Assert.Equal(first, TuiSpinner.WaveRgbAt(0, 0.35));
        Assert.NotEqual(first, second);
        Assert.NotEqual(second, third);
        Assert.All([first, second, third], rgb =>
        {
            Assert.InRange(rgb.R, 0, 255);
            Assert.InRange(rgb.G, 0, 255);
            Assert.InRange(rgb.B, 0, 255);
        });
    }

    [Fact]
    public void WaveRgb_RepeatsEveryCycleAndTravelsLeftToRight()
    {
        // The ramp wraps seamlessly: one cycle later every cell shows its old colour.
        Assert.Equal(TuiSpinner.WaveRgbAt(0, 0.3), TuiSpinner.WaveRgbAt(0, 0.3 + TuiSpinner.WaveCycleSeconds));
        Assert.Equal(TuiSpinner.WaveRgbAt(2, 0.3), TuiSpinner.WaveRgbAt(2, 0.3 + TuiSpinner.WaveCycleSeconds));

        // Cell 0 leads: what it shows now, cell 1 shows one spacing later.
        var spacing = TuiSpinner.WaveCellSpacing * TuiSpinner.WaveCycleSeconds;
        Assert.Equal(TuiSpinner.WaveRgbAt(0, 0.5), TuiSpinner.WaveRgbAt(1, 0.5 + spacing));
    }
}
