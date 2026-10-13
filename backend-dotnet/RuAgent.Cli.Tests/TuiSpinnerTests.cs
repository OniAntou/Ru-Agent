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
    public void WaveText_IsOneHorizontalBar()
    {
        Assert.Equal(new string('▬', TuiSpinner.WaveCells), TuiSpinner.WaveText);
        Assert.Equal(TuiSpinner.WaveCells, TuiText.VisibleWidth(TuiSpinner.WaveText));
    }

    [Fact]
    public void WaveRgb_IsDeterministicAndKeepsTheCellsDistinct()
    {
        // Every cell sits at its own ramp position: the row reads as one line with a
        // travelling gradient, not one tone.
        var cells = Enumerable.Range(0, TuiSpinner.WaveCells)
            .Select(cell => TuiSpinner.WaveRgbAt(cell, 0.35))
            .ToArray();

        Assert.Equal(cells[0], TuiSpinner.WaveRgbAt(0, 0.35));
        Assert.Equal(TuiSpinner.WaveCells, cells.Distinct().Count());
        Assert.All(cells, rgb =>
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
