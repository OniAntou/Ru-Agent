namespace RuAgent.Cli.Tui;

/// <summary>
/// The framed secret-input surface: title names the provider, the middle row
/// shows one bullet per entered character (count only, never content), and the
/// footer carries the hints. Every line occupies exactly the requested width.
/// The reader repaints only the mask row in place; the frame is printed once.
/// </summary>
public static class KeyInputPanel
{
    public const int MinWidth = 24;
    public const int MaxWidth = 1000;
    public const char MaskGlyph = '•';

    public static IReadOnlyList<string> Compose(
        int width,
        string providerDisplay,
        int bulletCount,
        string? footer = null)
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var label = string.IsNullOrWhiteSpace(providerDisplay) ? "api key" : providerDisplay.Trim();
        return [Top(w, label), MaskRow(w, Math.Max(0, bulletCount)), Bottom(w, footer ?? "ctrl+v paste · esc cancel")];
    }

    /// <summary>Zero-based row index of the mask line inside <see cref="Compose"/> output.</summary>
    public const int MaskRowIndex = 1;

    public static string ComposeMaskRow(int width, int bulletCount) =>
        MaskRow(Math.Clamp(width, MinWidth, MaxWidth), Math.Max(0, bulletCount));

    private static string MaskRow(int width, int bulletCount)
    {
        var inner = Math.Max(1, width - 4);
        var shown = Math.Min(bulletCount, inner - 2);
        var content = "  " + new string(MaskGlyph, shown);
        return Frame(width, TuiPalette.Paint(content, TuiToken.Bright), inner);
    }

    private static string Frame(int width, string content, int inner)
    {
        var used = TuiText.VisibleWidth(content);
        var body = used > inner ? TuiText.Truncate(content, inner) : content;
        var pad = Math.Max(0, inner - TuiText.VisibleWidth(body));
        return TuiPalette.Paint("│", TuiToken.Line)
            + " "
            + body
            + new string(' ', pad)
            + " "
            + TuiPalette.Paint("│", TuiToken.Line);
    }

    private static string Top(int width, string label)
    {
        var inner = width - 2;
        var left = "─ api key · " + label + " ";
        if (TuiText.VisibleWidth(left) > inner - 1)
        {
            label = TuiText.Truncate(label, Math.Max(1, inner - 16));
            left = "─ api key · " + label + " ";
        }

        var fill = Math.Max(0, inner - TuiText.VisibleWidth(left));
        return TuiPalette.Paint("╭", TuiToken.Line)
            + TuiPalette.Paint("─ ", TuiToken.Line)
            + TuiPalette.Paint("api key", TuiToken.Brand)
            + TuiPalette.Paint(" · " + label + " ", TuiToken.Line)
            + TuiPalette.Paint(new string('─', fill), TuiToken.Line)
            + TuiPalette.Paint("╮", TuiToken.Line);
    }

    private static string Bottom(int width, string footer)
    {
        var inner = width - 2;
        var hint = footer.Trim();
        string right = hint.Length > 0 ? " " + hint + " ─" : string.Empty;
        while (TuiText.VisibleWidth(right) > inner - 1)
        {
            if (hint.Length > 1)
            {
                hint = hint[..^1];
                right = " " + hint + " ─";
                continue;
            }

            right = string.Empty;
            break;
        }

        var fill = Math.Max(0, inner - TuiText.VisibleWidth(right));
        return TuiPalette.Paint("╰", TuiToken.Line)
            + TuiPalette.Paint(new string('─', fill), TuiToken.Line)
            + (right.Length == 0
                ? string.Empty
                : TuiPalette.Paint(right[..^2], TuiToken.Meta) + TuiPalette.Paint(" ─", TuiToken.Line))
            + TuiPalette.Paint("╯", TuiToken.Line);
    }
}
