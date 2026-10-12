namespace RuAgent.Cli.Tui;

/// <summary>
/// The shared animation clock: every phase is a pure function of elapsed seconds, so
/// a ticker at 18ms and one at 100ms produce the same motion. Call-counted frames
/// made the TG spinner advance at the 18ms meteor cadence and read as a jitter
/// instead of a rotation; deriving the frame from time removes that class of bug.
/// </summary>
public static class TuiSpinner
{
    /// <summary>Braille rotation used while the model thinks or writes.</summary>
    public static readonly IReadOnlyList<string> Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    /// <summary>Milliseconds each braille frame stays on screen.</summary>
    public const int FrameMilliseconds = 100;

    /// <summary>Half-filled circles for a running tool: the glyph itself turns.</summary>
    public static readonly IReadOnlyList<string> ToolGlyphs = ["◐", "◓", "◑", "◒"];

    /// <summary>Milliseconds each tool glyph stays on screen.</summary>
    public const int ToolGlyphMilliseconds = 250;

    /// <summary>The braille frame for an elapsed time; negative values clamp to the first frame.</summary>
    public static string FrameAt(double elapsedSeconds) => Frames[StepAt(elapsedSeconds, FrameMilliseconds, Frames.Count)];

    /// <summary>The running-tool glyph for an elapsed time.</summary>
    public static string ToolGlyphAt(double elapsedSeconds) =>
        ToolGlyphs[StepAt(elapsedSeconds, ToolGlyphMilliseconds, ToolGlyphs.Count)];

    private static int StepAt(double elapsedSeconds, int stepMilliseconds, int steps)
    {
        if (elapsedSeconds <= 0)
        {
            return 0;
        }

        var step = (long)(elapsedSeconds * 1000 / stepMilliseconds);
        return (int)(step % steps);
    }
}
