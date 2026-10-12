namespace RuAgent.Cli.Tui;

public sealed record TuiActivitySpan(string Text, TuiToken Tone);

/// <summary>Semantic activity parts shared by the status line and the transcript's live row.</summary>
public sealed record TuiActivityPresentation(
    string Glyph, string Action, string? Target, string Metrics, TuiToken Tone)
{
    public IReadOnlyList<TuiActivitySpan> Fit(int width)
    {
        if (width <= 0) return [];
        var glyph = ChatDisplayText.Sanitize(Glyph, 8);
        var action = ChatDisplayText.Sanitize(Action, 128);
        var target = ChatDisplayText.Sanitize(Target ?? string.Empty, 512);
        var metrics = ChatDisplayText.Sanitize(Metrics, 128);
        var head = glyph + " " + action;
        if (width < 32 || TuiText.VisibleWidth(head) + TuiText.VisibleWidth(metrics) + 4 > width)
            metrics = string.Empty;
        var rightWidth = TuiText.VisibleWidth(metrics);
        var leftWidth = width - rightWidth - (rightWidth > 0 ? 3 : 0);
        head = TuiText.Truncate(head, leftWidth);
        var spans = new List<TuiActivitySpan>();
        if (head.StartsWith(glyph + " ", StringComparison.Ordinal))
        {
            spans.Add(new(glyph, Tone));
            spans.Add(new(head[glyph.Length..], Tone == TuiToken.Brand ? TuiToken.Bright : Tone));
        }
        else spans.Add(new(head, Tone));
        var targetRoom = leftWidth - TuiText.VisibleWidth(head) - 2;
        if (target.Length > 0 && targetRoom > 2)
            spans.Add(new("  " + TuiText.MiddleTruncate(target, targetRoom), TuiToken.Meta));
        var used = spans.Sum(span => TuiText.VisibleWidth(span.Text));
        if (rightWidth > 0)
        {
            spans.Add(new(new string(' ', width - used - rightWidth), TuiToken.Line));
            spans.Add(new(metrics, TuiToken.Meta));
        }
        return spans;
    }
}
