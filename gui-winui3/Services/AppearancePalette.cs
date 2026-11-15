using Windows.UI;
using static RuApp.Services.RuThemePalettes;

namespace RuApp.Services;

/// <summary>
/// Builds the palette Ru paints with from a base theme pack and the user's color
/// overrides. Colors the user did not pick are derived from the ones they did, and a
/// contrast floor keeps text readable whatever they chose: a user who sets the text
/// color to the canvas color still gets a Settings page they can read and undo.
/// </summary>
public static class AppearancePalette
{
    /// <summary>WCAG AA for body text.</summary>
    public const double MinimumTextContrast = 4.5;

    /// <summary>WCAG AA for large text and UI components: secondary text, accent fills.</summary>
    public const double MinimumSecondaryContrast = 3.0;

    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);

    public static ThemePalette Build(ThemePalette basePalette, AppearanceColorSet? overrides, bool isDark)
    {
        var set = (overrides ?? AppearanceColorSet.Empty).Sanitized();
        if (set.IsEmpty) return basePalette;

        bool canvasChanged = AppearanceColor.TryParse(set.Canvas, out var canvas);
        if (!canvasChanged) canvas = basePalette.Canvas;

        var sidebar = Pick(set.Sidebar, canvasChanged
            ? Mix(canvas, Black, isDark ? 0.2 : 0.05)
            : basePalette.Sidebar);
        var surface = Pick(set.Surface, canvasChanged
            ? Mix(canvas, White, isDark ? 0.06 : 0.4)
            : basePalette.Surface);

        var ink = EnsureContrast(Pick(set.Text, basePalette.Ink), MinimumTextContrast, canvas, surface, sidebar);
        var muted = EnsureContrast(Pick(set.MutedText, basePalette.Muted), MinimumSecondaryContrast, canvas, surface);
        var line = Pick(set.Border, canvasChanged ? Mix(canvas, ink, 0.16) : basePalette.Line);

        bool accentChanged = AppearanceColor.TryParse(set.Accent, out var accent);
        if (!accentChanged) accent = basePalette.Accent;
        // Accent fills carry canvas-colored text (RuOnAccentBrush), so the floor is
        // measured against the canvas.
        accent = EnsureContrast(accent, MinimumSecondaryContrast, canvas);
        bool accentDerived = accentChanged || canvasChanged;
        var accentSoft = accentDerived ? Mix(canvas, accent, 0.12) : basePalette.AccentSoft;
        var accentHover = accentDerived ? Mix(accent, White, 0.15) : basePalette.AccentHover;
        var accentPressed = accentDerived ? Mix(accent, Black, 0.15) : basePalette.AccentPressed;

        return basePalette with
        {
            Canvas = canvas,
            Sidebar = sidebar,
            Surface = surface,
            Ink = ink,
            Muted = muted,
            Line = line,
            Accent = accent,
            AccentSoft = accentSoft,
            AccentHover = accentHover,
            AccentPressed = accentPressed,
            ToggleOn = accentDerived ? accent : basePalette.ToggleOn,
            ToggleOver = accentDerived ? accentHover : basePalette.ToggleOver,
            TogglePressed = accentDerived ? accentPressed : basePalette.TogglePressed,
            SliderValue = accentDerived ? accent : basePalette.SliderValue,
            SliderValueOver = accentDerived ? accentHover : basePalette.SliderValueOver,
            SliderThumb = accentDerived ? accent : basePalette.SliderThumb,
            SliderThumbOver = accentDerived ? accentHover : basePalette.SliderThumbOver,
        };
    }

    /// <summary>
    /// Moves <paramref name="foreground"/> toward black or white, whichever reaches the
    /// floor against every background with the smaller change, and returns the first
    /// color that clears it. An already-readable color comes back unchanged.
    /// </summary>
    public static Color EnsureContrast(Color foreground, double minimum, params Color[] backgrounds)
    {
        if (MinContrast(foreground, backgrounds) >= minimum) return foreground;
        Color? best = null;
        foreach (var target in new[] { Black, White })
        {
            for (int step = 1; step <= 20; step++)
            {
                var candidate = Mix(foreground, target, step / 20.0);
                if (MinContrast(candidate, backgrounds) < minimum) continue;
                if (best is null || Distance(candidate, foreground) < Distance(best.Value, foreground)) best = candidate;
                break;
            }
        }

        // Neither pole clears every background (two backgrounds at opposite ends):
        // take whichever pole reads better against the worst of them.
        return best ?? (MinContrast(Black, backgrounds) >= MinContrast(White, backgrounds) ? Black : White);
    }

    public static double ContrastRatio(Color a, Color b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    public static Color Mix(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        static byte Lerp(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromArgb(255, Lerp(from.R, to.R, amount), Lerp(from.G, to.G, amount), Lerp(from.B, to.B, amount));
    }

    private static Color Pick(string? hex, Color fallback) =>
        AppearanceColor.TryParse(hex, out var color) ? color : fallback;

    private static double MinContrast(Color foreground, Color[] backgrounds) =>
        backgrounds.Min(background => ContrastRatio(foreground, background));

    private static double Distance(Color a, Color b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
