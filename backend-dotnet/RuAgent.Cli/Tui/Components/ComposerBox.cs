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
/// The prompt frame: the three-row sequential composer (borders, prompt, buffer) and the
/// full-screen composer stack with its hint row. Pure composition: every line occupies
/// exactly the requested width, so the transcript above can never be pushed out of
/// alignment by the composer.
/// </summary>
public static class ComposerBox
{
    public const int Height = 3;
    public const int MinWidth = 10;
    public const int MaxWidth = 1000;
    internal static readonly (string Text, TuiToken? Token)[] HintKeySegments =
    [
        ("/", TuiToken.Bright),
        (" commands", TuiToken.Meta),
        ("  ", null),
        ("tab", TuiToken.Bright),
        (" complete", TuiToken.Meta),
        ("  ", null),
        ("ctrl+o", TuiToken.Bright),
        (" expand", TuiToken.Meta),
    ];

    /// <summary>Rows of the full-screen composer stack: top border, padding, input, padding, meta, bottom border.</summary>
    public const int StackHeight = 6;

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
    /// The full-screen composer stack: top border, the input row (with the elevated element
    /// background), the agent/model/provider meta row and the bottom border. Every row is
    /// exactly <paramref name="width"/> cells.
    /// </summary>
    public static IReadOnlyList<string> ComposeStack(
        int width,
        ComposerState state,
        string buffer,
        string ghost,
        string agent,
        string model,
        string provider)
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var token = TokenFor(state);
        return
        [
            Top(w, token, null),
            BlankRow(w, token),
            InputRow(w, token, buffer ?? string.Empty, ghost ?? string.Empty),
            BlankRow(w, token),
            MetaRow(w, token, agent, model, provider),
            Bottom(w, token, string.Empty, string.Empty),
        ];
    }

    /// <summary>One-based caret column inside <see cref="ComposeStack"/>'s input row.</summary>
    public static int StackCursorColumn(int width, string buffer)
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var shown = TuiText.Tail(buffer ?? string.Empty, Math.Max(1, w - 4));
        return Math.Min(w - 1, 3 + TuiText.VisibleWidth(shown));
    }

    /// <summary>
    /// The hint row below the composer: the colour-wave busy indicator with
    /// esc-interrupt on the left while a turn runs (the workspace label when idle), key
    /// hints right-aligned. Exactly <paramref name="width"/> cells.
    /// </summary>
    public static string ComposeHints(int width, bool busy, string idleLeft)
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var right = string.Concat(HintKeySegments.Select(segment => segment.Token is { } token
            ? TuiPalette.Paint(segment.Text, token)
            : segment.Text));
        var rightWidth = TuiText.VisibleWidth(right);

        var left = busy
            ? TuiPalette.Paint(TuiSpinner.WaveText, TuiToken.Aurora)
                + " "
                + TuiPalette.Paint("esc", TuiToken.Bright)
                + TuiPalette.Paint(" interrupt", TuiToken.Meta)
            : TuiPalette.Paint(idleLeft ?? string.Empty, TuiToken.Meta);

        var leftBudget = Math.Max(0, w - rightWidth - 2);
        if (TuiText.VisibleWidth(left) > leftBudget)
        {
            left = TuiText.Truncate(left, leftBudget);
        }

        var gap = Math.Max(1, w - TuiText.VisibleWidth(left) - rightWidth);
        return TuiText.Pad(left + new string(' ', gap) + right, w);
    }

    private static string BlankRow(int width, TuiToken token)
    {
        var inner = Math.Max(0, width - 2);
        return TuiPalette.Paint("│", token)
            + TuiPalette.ElementBgCode()
            + new string(' ', inner)
            + TuiPalette.Reset
            + TuiPalette.Paint("│", token);
    }

    private static string InputRow(int width, TuiToken token, string buffer, string ghost)
    {
        var inner = Math.Max(1, width - 4);
        var text = TuiText.Tail(buffer, inner);
        var used = TuiText.VisibleWidth(text);
        var ghostText = string.IsNullOrEmpty(ghost) || used >= inner
            ? string.Empty
            : TuiText.Truncate(ghost, inner - used);
        var pad = Math.Max(0, inner - used - TuiText.VisibleWidth(ghostText));
        var bg = TuiPalette.ElementBgCode();
        // Paint() ends every segment with a reset, which would punch dark holes into the
        // gray band; re-apply the background after each segment so the row is one surface.
        return TuiPalette.Paint("│", token)
            + bg
            + " "
            + TuiPalette.Paint(text, TuiToken.Bright)
            + bg
            + (ghostText.Length == 0 ? string.Empty : TuiPalette.Paint(ghostText, TuiToken.Meta) + bg)
            + new string(' ', pad)
            + " "
            + TuiPalette.Reset
            + TuiPalette.Paint("│", token);
    }

    private static string MetaRow(int width, TuiToken token, string agent, string model, string provider)
    {
        var inner = Math.Max(1, width - 4);
        var name = TuiText.Truncate(
            string.IsNullOrWhiteSpace(agent) ? "Ru" : agent.Trim(),
            Math.Max(1, inner - 2));
        // Every painted segment ends with a reset; the background is re-applied after each
        // so the gray band spans the whole meta row, separators and padding included.
        var bg = TuiPalette.ElementBgCode();
        var parts = TuiPalette.Paint(name, TuiToken.Brand) + bg;
        var used = TuiText.VisibleWidth(parts);

        if (!string.IsNullOrWhiteSpace(model))
        {
            var modelText = TuiText.Truncate(model.Trim(), Math.Max(1, inner - used - 2));
            parts += "  " + TuiPalette.Paint(modelText, TuiToken.Bright) + bg;
            used += 2 + TuiText.VisibleWidth(modelText);
        }

        if (!string.IsNullOrWhiteSpace(provider) && used < inner - 2)
        {
            var providerText = TuiText.Truncate(ProviderLabel(provider), Math.Max(1, inner - used - 1));
            parts += " " + TuiPalette.Paint(providerText, TuiToken.Meta) + bg;
            used += 1 + TuiText.VisibleWidth(providerText);
        }

        var pad = Math.Max(0, inner - used);
        return TuiPalette.Paint("│", token)
            + bg
            + " "
            + parts
            + new string(' ', pad)
            + " "
            + TuiPalette.Reset
            + TuiPalette.Paint("│", token);
    }

    /// <summary>
    /// Provider text for the meta row: the model-gateway prefix is dropped, so
    /// <c>models.dev:kilo</c> reads as <c>kilo</c> while plain ids stay untouched.
    /// </summary>
    private static string ProviderLabel(string provider)
    {
        var trimmed = provider.Trim();
        const string gatewayPrefix = "models.dev:";
        return trimmed.StartsWith(gatewayPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[gatewayPrefix.Length..]
            : trimmed;
    }

    private static string Top(int width, TuiToken token, string? topLabel)
    {
        var inner = width - 2;
        if (string.IsNullOrWhiteSpace(topLabel))
        {
            return TuiPalette.Paint("┌" + new string('─', inner) + "┐", token);
        }

        var label = topLabel.Trim();
        var left = "─ " + label + " ";
        if (TuiText.VisibleWidth(left) > inner - 1)
        {
            label = TuiText.Truncate(label, Math.Max(1, inner - 4));
            left = "─ " + label + " ";
        }

        var fill = Math.Max(0, inner - TuiText.VisibleWidth(left));
        return TuiPalette.Paint("┌", token)
            + TuiPalette.Paint("─ ", token)
            + TuiPalette.Paint(label, TuiToken.Brand)
            + TuiPalette.Paint(" ", token)
            + TuiPalette.Paint(new string('─', fill), token)
            + TuiPalette.Paint("┐", token);
    }

    /// <summary>
    /// Shared row metrics: the legend slice <see cref="Compose"/> paints and the width the
    /// buffer may use. <see cref="CursorColumn"/> consumes the same width, so the caret can
    /// never be placed over the legend.
    /// </summary>
    private static (string LegendText, int LegendWidth, int Available) RowMetrics(
        int width,
        string prompt,
        string? legend)
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        var inner = Math.Max(1, w - 4);
        var legendText = string.IsNullOrWhiteSpace(legend) ? string.Empty : legend.Trim();
        var legendWidth = string.IsNullOrEmpty(legendText) ? 0 : Math.Min(TuiText.VisibleWidth(legendText), Math.Max(0, inner / 2));
        if (legendWidth > 0 && TuiText.VisibleWidth(legendText) > legendWidth)
        {
            legendText = TuiText.Tail(legendText, legendWidth);
        }

        var room = Math.Max(1, inner - (legendWidth > 0 ? legendWidth + 1 : 0));
        var available = Math.Max(1, room - TuiText.VisibleWidth(prompt ?? string.Empty));
        return (legendText, legendWidth, available);
    }

    /// <summary>
    /// One-based column where the caret belongs inside <see cref="Compose"/>'s middle row,
    /// honouring the tail the box shows when the buffer overflows and the legend the row
    /// reserves. Placing the real terminal cursor here keeps IME candidate windows attached
    /// to the text being typed.
    /// </summary>
    public static int CursorColumn(int width, string prompt, string buffer, string legend = "")
    {
        var w = Math.Clamp(width, MinWidth, MaxWidth);
        prompt ??= string.Empty;
        var (_, _, available) = RowMetrics(w, prompt, legend);
        var promptWidth = TuiText.VisibleWidth(prompt);
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
        return TuiPalette.Paint("└", token)
            + (left.Length == 0 ? string.Empty : TuiPalette.Paint("─ ", token) + TuiPalette.Paint(left[2..], TuiToken.Meta))
            + TuiPalette.Paint(new string('─', fill), token)
            + (right.Length == 0 ? string.Empty : TuiPalette.Paint(right[..^2], TuiToken.Meta) + TuiPalette.Paint(" ─", token))
            + TuiPalette.Paint("┘", token);
    }
}
