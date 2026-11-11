namespace RuApp.Services;

/// <summary>
/// The colour wave that runs through the startup wordmark. Each letter takes its colour
/// from a point on a looping ink, green, blue, ink ramp, and the points are offset along
/// the word so the colour appears to travel through it. Pure and deterministic; no UI.
/// </summary>
internal static class StartupWordmarkWave
{
    /// <summary>Seconds for one colour to cross the ramp once.</summary>
    public const double PeriodSeconds = 3.6;

    /// <summary>How far along the ramp each following letter sits (a share of a loop).</summary>
    public const double LetterOffset = 0.085;

    public readonly record struct Rgb(byte R, byte G, byte B);

    public static Rgb ColorAt(int letterIndex, double seconds, Rgb ink, Rgb green, Rgb blue)
    {
        if (!double.IsFinite(seconds)) seconds = 0;
        double p = seconds / PeriodSeconds - letterIndex * LetterOffset;
        p -= Math.Floor(p);
        return p switch
        {
            < 1.0 / 3 => Mix(ink, green, p * 3),
            < 2.0 / 3 => Mix(green, blue, p * 3 - 1),
            _ => Mix(blue, ink, p * 3 - 2),
        };
    }

    private static Rgb Mix(Rgb from, Rgb to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        t = t * t * (3 - 2 * t);
        return new Rgb(Lerp(from.R, to.R, t), Lerp(from.G, to.G, t), Lerp(from.B, to.B, t));
    }

    private static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
}