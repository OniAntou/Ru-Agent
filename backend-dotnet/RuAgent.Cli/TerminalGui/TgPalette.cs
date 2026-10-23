using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Maps the classic palette onto Terminal.Gui attributes. Every visual role is pinned:
/// the framework derives Focus/Editable by swapping colours when they are unset, which
/// turned the composer into a bright bar during the spike.
/// </summary>
public static class TgPalette
{
    public static Color ToColor(TuiToken token)
    {
        var (r, g, b) = TuiPalette.Rgb(token);
        return new Color(r, g, b, 255);
    }

    public static Color ToColor((int R, int G, int B) rgb) =>
        new(Math.Clamp(rgb.R, 0, 255), Math.Clamp(rgb.G, 0, 255), Math.Clamp(rgb.B, 0, 255), 255);

    /// <summary>
    /// The floating card's cool neutral fill separates it from the prompt surface.
    /// </summary>
    public static (int R, int G, int B) CardBgRgb() => (25, 32, 32);

    /// <summary>The filled bar behind the chosen card entry, with dark text on top of it.</summary>
    public static (int R, int G, int B) CardSelectionRgb() => (154, 199, 177);

    /// <summary>Foreground used on top of <see cref="CardSelectionRgb"/>.</summary>
    public static (int R, int G, int B) CardOnSelectionRgb() => (16, 16, 16);

    public static Attr Attr(TuiToken fg, TuiToken? bg = null) =>
        new(ToColor(fg), bg is { } b ? ToColor(b) : new Color(0, 0, 0, 255));

    /// <summary>Per-cell truecolor foreground over the row's black background.</summary>
    public static Attr Attr((int R, int G, int B) fg) =>
        new(ToColor(fg), new Color(0, 0, 0, 255));

    public static Attr Attr(TuiToken fg, (int R, int G, int B) bg) =>
        new(ToColor(fg), ToColor(bg));

    public static Attr Attr((int R, int G, int B) fg, (int R, int G, int B) bg) =>
        new(ToColor(fg), ToColor(bg));

    public static Scheme Scheme(TuiToken fg)
    {
        var attr = Attr(fg);
        return new Scheme(attr)
        {
            Focus = attr,
            Editable = attr,
            Active = attr,
        };
    }

    public static Scheme Scheme(TuiToken fg, TuiToken bg)
    {
        var attr = Attr(fg, bg);
        return new Scheme(attr)
        {
            Focus = attr,
            Editable = attr,
            Active = attr,
        };
    }

    public static Scheme Scheme(TuiToken fg, (int R, int G, int B) bg)
    {
        var attr = Attr(fg, bg);
        return new Scheme(attr)
        {
            Focus = attr,
            Editable = attr,
            Active = attr,
        };
    }
}
