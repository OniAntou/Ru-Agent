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
}
