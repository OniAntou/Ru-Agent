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

    public static Color ToColor((int R, int G, int B) rgb) => new(rgb.R, rgb.G, rgb.B, 255);

    public static Attr Attr(TuiToken fg, TuiToken? bg = null) =>
        new(ToColor(fg), bg is { } b ? ToColor(b) : new Color(0, 0, 0, 255));

    public static Attr Attr(TuiToken fg, (int R, int G, int B) bg) =>
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
