namespace RuAgent.Cli.Tui;

/// <summary>Composer frame states; the border colour and labels follow the state.</summary>
public enum ComposerState
{
    Idle,
    Slash,
    Busy,
    Queued,
    Approval,
    Error,
}

/// <summary>
/// The three-row prompt frame. Pure composition: every line occupies exactly the requested
/// width, so the transcript above can never be pushed out of alignment by the composer.
/// </summary>
public static class ComposerBox
{
    public const int Height = 3;
    public const int MinWidth = 10;
    public const int MaxWidth = 1000;

    public static TuiToken TokenFor(ComposerState state) => state switch
    {
        ComposerState.Slash or ComposerState.Busy or ComposerState.Queued => TuiToken.Brand,
        ComposerState.Approval => TuiToken.Warn,
        ComposerState.Error => TuiToken.Err,
        _ => TuiToken.Line,
    };

    public static IReadOnlyList<string> Compose(
        int width,
        ComposerState state,
        string prompt,
        string buffer,
        string ghost,
        string legend,
        string hint,
        string? topLabel)
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var token = TokenFor(state);
        prompt ??= string.Empty;
        buffer ??= string.Empty;
        return
        [
            Top(w, token, topLabel),
            Middle(w, token, prompt, buffer, ghost ?? string.Empty),
            Bottom(w, token, legend, hint),
        ];
    }

    /// <summary>
    /// The single-row composer used by the full-screen footer: the same middle row the
    /// three-row frame paints (borders, prompt, buffer, tail-anchored text) plus an
    /// optional right-aligned legend, exactly <paramref name="width"/> cells so the
    /// footer never shifts.
    /// </summary>
    public static string ComposeRow(
        int width,
        ComposerState state,
        string prompt,
        string buffer,
        string ghost = "",
        string legend = "")
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var token = TokenFor(state);
        prompt ??= string.Empty;
        buffer ??= string.Empty;
        ghost ??= string.Empty;

        var inner = Math.Max(1, w - 4);
        var legendText = string.IsNullOrWhiteSpace(legend) ? string.Empty : legend.Trim();
        var legendWidth = string.IsNullOrEmpty(legendText) ? 0 : Math.Min(TuiText.VisibleWidth(legendText), Math.Max(0, inner / 2));
        if (legendWidth > 0 && TuiText.VisibleWidth(legendText) > legendWidth)
        {
            legendText = TuiText.Tail(legendText, legendWidth);
        }

        var room = Math.Max(1, inner - (legendWidth > 0 ? legendWidth + 1 : 0));
        var promptWidth = TuiText.VisibleWidth(prompt);
        var available = Math.Max(1, room - promptWidth);
        var text = TuiText.Tail(buffer, available);
        var used = TuiText.VisibleWidth(text);
        var ghostText = string.IsNullOrEmpty(ghost) || used >= available
            ? string.Empty
            : TuiText.Truncate(ghost, available - used);
        var pad = Math.Max(0, inner - promptWidth - used - TuiText.VisibleWidth(ghostText) - (legendWidth > 0 ? legendWidth + 1 : 0));

        return TuiPalette.Paint("│ ", token)
            + prompt
            + TuiPalette.Paint(text, TuiToken.Bright)
            + (ghostText.Length == 0 ? string.Empty : TuiPalette.Paint(ghostText, TuiToken.Meta))
            + new string(' ', pad)
            + (legendWidth > 0 ? " " + TuiPalette.Paint(legendText, TuiToken.Meta) : string.Empty)
            + TuiPalette.Paint(" │", token);
    }

    private static string Top(int width, TuiToken token, string? topLabel)
    {
        var inner = width - 2;
        if (string.IsNullOrWhiteSpace(topLabel))
        {
            return TuiPalette.Paint("╭" + new string('─', inner) + "╮", token);
        }

        var label = topLabel.Trim();
        var left = "─ " + label + " ";
        if (TuiText.VisibleWidth(left) > inner - 1)
        {
            label = TuiText.Truncate(label, Math.Max(1, inner - 4));
            left = "─ " + label + " ";
        }

        var fill = Math.Max(0, inner - TuiText.VisibleWidth(left));
        return TuiPalette.Paint("╭", token)
            + TuiPalette.Paint("─ ", token)
            + TuiPalette.Paint(label, TuiToken.Brand)
            + TuiPalette.Paint(" ", token)
            + TuiPalette.Paint(new string('─', fill), token)
            + TuiPalette.Paint("╮", token);
    }

    /// <summary>
    /// One-based column where the caret belongs inside <see cref="Middle"/>, honouring the tail
    /// the box shows when the buffer overflows. Placing the real terminal cursor here keeps IME
    /// candidate windows attached to the text being typed.
    /// </summary>
    public static int CursorColumn(int width, string prompt, string buffer)
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var inner = Math.Max(1, w - 4);
        prompt ??= string.Empty;
        var promptWidth = TuiText.VisibleWidth(prompt);
        var available = Math.Max(1, inner - promptWidth);
        var shown = TuiText.Tail(buffer ?? string.Empty, available);

        // Frame border + gutter + prompt + the visible slice of the buffer.
        return Math.Min(w - 1, 3 + promptWidth + TuiText.VisibleWidth(shown));
    }

    private static string Middle(int width, TuiToken token, string prompt, string buffer, string ghost)
    {
        // Left/right frames plus the one-space gutter on each side.
        var inner = Math.Max(1, width - 4);
        var promptWidth = TuiText.VisibleWidth(prompt);
        var available = Math.Max(1, inner - promptWidth);
        var text = TuiText.Tail(buffer, available);
        var used = TuiText.VisibleWidth(text);
        var ghostText = string.IsNullOrEmpty(ghost) || used >= available
            ? string.Empty
            : TuiText.Truncate(ghost, available - used);
        var pad = Math.Max(0, inner - promptWidth - used - TuiText.VisibleWidth(ghostText));

        return TuiPalette.Paint("│ ", token)
            + prompt
            + TuiPalette.Paint(text, TuiToken.Bright)
            + (ghostText.Length == 0 ? string.Empty : TuiPalette.Paint(ghostText, TuiToken.Meta))
            + new string(' ', pad)
            + TuiPalette.Paint(" │", token);
    }

    private static string Bottom(int width, TuiToken token, string legend, string hint)
    {
        var inner = width - 2;
        var legendText = string.IsNullOrWhiteSpace(legend) ? string.Empty : legend.Trim();
        var hintText = string.IsNullOrWhiteSpace(hint) ? string.Empty : hint.Trim();

        string left = legendText.Length > 0 ? "─ " + legendText + " " : string.Empty;
        string right = hintText.Length > 0 ? " " + hintText + " ─" : string.Empty;

        // Shed the hint first, then trim the legend, so the frame always closes.
        while (TuiText.VisibleWidth(left) + TuiText.VisibleWidth(right) > inner)
        {
            if (right.Length > 0)
            {
                right = string.Empty;
                continue;
            }

            if (legendText.Length > 1)
            {
                legendText = legendText[..^1];
                left = "─ " + legendText + " ";
                continue;
            }

            left = string.Empty;
            break;
        }

        var fill = Math.Max(0, inner - TuiText.VisibleWidth(left) - TuiText.VisibleWidth(right));
        return TuiPalette.Paint("╰", token)
            + (left.Length == 0 ? string.Empty : TuiPalette.Paint("─ ", token) + TuiPalette.Paint(left[2..], TuiToken.Meta))
            + TuiPalette.Paint(new string('─', fill), token)
            + (right.Length == 0 ? string.Empty : TuiPalette.Paint(right[..^2], TuiToken.Meta) + TuiPalette.Paint(" ─", token))
            + TuiPalette.Paint("╯", token);
    }
}
