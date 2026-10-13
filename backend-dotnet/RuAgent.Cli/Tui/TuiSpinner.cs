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

    /// <summary>The fixed line the busy hint row waves colour through.</summary>
    public const string WaveGlyph = "━";

    /// <summary>Cells in the busy hint wave line.</summary>
    public const int WaveCells = 5;

    /// <summary>The wave's visible text: <see cref="WaveCells"/> joined line cells.</summary>
    public static readonly string WaveText = string.Concat(Enumerable.Repeat(WaveGlyph, WaveCells));

    /// <summary>Milliseconds each wave phase bucket stays on screen.</summary>
    public const int WaveStepMilliseconds = 90;

    /// <summary>Seconds for one full traversal of the colour ramp.</summary>
    public const double WaveCycleSeconds = 1.4;

    /// <summary>Phase offset between neighbouring cells, as a fraction of the ramp.</summary>
    public const double WaveCellSpacing = 0.1;

    /// <summary>Brightness floor of a cell, so the trailing dots stay visible.</summary>
    private const double WaveFloor = 0.45;

    /// <summary>The braille frame for an elapsed time; negative values clamp to the first frame.</summary>
    public static string FrameAt(double elapsedSeconds) => Frames[StepAt(elapsedSeconds, FrameMilliseconds, Frames.Count)];

    /// <summary>The running-tool glyph for an elapsed time.</summary>
    public static string ToolGlyphAt(double elapsedSeconds) =>
        ToolGlyphs[StepAt(elapsedSeconds, ToolGlyphMilliseconds, ToolGlyphs.Count)];

    /// <summary>
    /// The wave phase bucket for an elapsed time: a caller redraws only when it changes,
    /// so an 18 ms ticker repaints the hint row about eleven times a second.
    /// </summary>
    public static long WaveBucketAt(double elapsedSeconds) =>
        elapsedSeconds <= 0 ? 0 : (long)(elapsedSeconds * 1000 / WaveStepMilliseconds);

    /// <summary>
    /// The ramp colour of one wave cell at an elapsed time. Cell 0 leads, so the crest
    /// travels left to right as the phase advances; the trailing cells sit further back
    /// on the ramp and a sine envelope fades them, which reads as one moving glow
    /// instead of three blinking dots.
    /// </summary>
    public static (int R, int G, int B) WaveRgbAt(int cell, double elapsedSeconds)
    {
        var phase = elapsedSeconds <= 0 ? 0 : elapsedSeconds / WaveCycleSeconds;
        var position = phase - (cell * WaveCellSpacing);
        var p = position - Math.Floor(position);
        var (r, g, b) = RampAt(p);
        var brightness = WaveFloor + ((1 - WaveFloor) * Math.Sin(Math.PI * p));
        return (
            (int)Math.Round(r * brightness),
            (int)Math.Round(g * brightness),
            (int)Math.Round(b * brightness));
    }

    /// <summary>Aurora → brand → brand2 → aurora: the cycle has no visible seam.</summary>
    private static (int R, int G, int B) RampAt(double p)
    {
        var scaled = p * 3;
        var index = Math.Clamp((int)scaled, 0, 2);
        var t = scaled - index;
        var (r1, g1, b1) = TuiPalette.Rgb(index switch
        {
            0 => TuiToken.Aurora,
            1 => TuiToken.Brand,
            _ => TuiToken.Brand2,
        });
        var (r2, g2, b2) = TuiPalette.Rgb(index switch
        {
            0 => TuiToken.Brand,
            1 => TuiToken.Brand2,
            _ => TuiToken.Aurora,
        });
        return (Lerp(r1, r2, t), Lerp(g1, g2, t), Lerp(b1, b2, t));
    }

    private static int Lerp(int from, int to, double t) => (int)Math.Round(from + ((to - from) * t));

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
