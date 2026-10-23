namespace RuAgent.Cli.Tui;

public sealed record TuiActivitySpan(string Text, TuiToken Tone);

/// <summary>
/// Semantic parts of the transcript's live row. It shares the tool row's geometry: two
/// cells of feed indent, a state glyph, a muted action, the brightest target and receding
/// metrics — so the row that settles and the row that replaces it line up cell for cell.
/// </summary>
public sealed record TuiActivityPresentation(
    string Glyph, string Action, string? Target, string Metrics, TuiToken Tone)
{
    /// <summary>Feed rows sit two cells inside the transcript's text column.</summary>
    public const int Indent = 2;

    public IReadOnlyList<TuiActivitySpan> Fit(int width)
    {
        if (width <= 0) return [];
        var glyph = ChatDisplayText.Sanitize(Glyph, 8);
        var action = ChatDisplayText.Sanitize(Action, 128);
        var target = ChatDisplayText.Sanitize(Target ?? string.Empty, 512);
        var metrics = ChatDisplayText.Sanitize(Metrics, 128);

        var w = Math.Max(1, width);
        // Below the feed's minimum layout the indent is dropped before the content is.
        var indent = w >= Indent + 8 ? Indent : 0;
        var usable = w - indent;
        if (usable < 32 || TuiText.VisibleWidth(glyph) + 1 + TuiText.VisibleWidth(action)
            + TuiText.VisibleWidth(metrics) + 4 > usable)
        {
            metrics = string.Empty;
        }

        var rightWidth = TuiText.VisibleWidth(metrics);
        var leftRoom = Math.Max(1, usable - rightWidth - (rightWidth > 0 ? 3 : 0));
        var head = TuiText.Truncate(glyph + " " + action, leftRoom);

        var spans = new List<TuiActivitySpan>(8);
        if (indent > 0)
        {
            spans.Add(new(new string(' ', indent), TuiToken.Line));
        }

        if (head.StartsWith(glyph, StringComparison.Ordinal))
        {
            spans.Add(new(glyph, Tone));
            if (head.Length > glyph.Length)
            {
                spans.Add(new(head[glyph.Length..], TuiToken.Meta));
            }
        }
        else
        {
            spans.Add(new(head, Tone));
        }

        var used = indent + TuiText.VisibleWidth(head);
        var targetRoom = leftRoom - TuiText.VisibleWidth(head) - 2;
        if (target.Length > 0 && targetRoom > 2)
        {
            var clipped = TuiText.MiddleTruncate(target, targetRoom);
            if (clipped.Length > 0)
            {
                spans.Add(new("  " + clipped, TuiToken.Ink));
                used += 2 + TuiText.VisibleWidth(clipped);
            }
        }

        if (rightWidth > 0)
        {
            spans.Add(new(new string(' ', Math.Max(1, w - used - rightWidth)), TuiToken.Line));
            spans.Add(new(metrics, TuiToken.Meta));
        }

        return spans;
    }
}
