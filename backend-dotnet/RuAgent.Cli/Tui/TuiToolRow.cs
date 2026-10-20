namespace RuAgent.Cli.Tui;

/// <summary>
/// The inline tool row cloned from OpenCode: a one-cell state glyph followed by the tool
/// display name, its target and bounded metrics. Settled rows read <c>✓</c> in the ok
/// tone, failures flip to <c>✗</c>, and a running row turns its half-filled circle.
/// Pure string-in/line-out so snapshot tests can pin the output.
/// </summary>
public static class TuiToolRow
{
    /// <summary>Narrowest row this renderer produces; requested widths below this clamp up.</summary>
    public const int MinWidth = 14;

    /// <summary>
    /// One settled tool row: <c>{glyph} {Display} {target} {meta}</c>, truncated to
    /// <paramref name="width"/> cells. Meta is already composed (e.g. <c>(18 matches)</c>)
    /// by the transcript; a blank value leaves it off.
    /// </summary>
    public static string Compose(
        ToolLineStatus status, string display, string? target, string? meta, int width)
    {
        return ComposeCore(status, display, target, meta, width, true);
    }

    public static TranscriptLine ComposePresentation(ToolLineStatus status, string display, string? target, string? meta, int width) =>
        TranscriptLine.Plain(ComposeCore(status, display, target, meta, width, false), TranscriptRole.Tool);

    private static string ComposeCore(ToolLineStatus status, string display, string? target, string? meta, int width, bool colored)
    {
        string Paint(string text, TuiToken token) => colored ? TuiPalette.Paint(text, token) : text;
        ArgumentException.ThrowIfNullOrWhiteSpace(display);
        var w = Math.Max(MinWidth, width);
        var token = status == ToolLineStatus.Failed ? TuiToken.Err
            : status == ToolLineStatus.Unknown ? TuiToken.Meta
            : status == ToolLineStatus.Running ? TuiToken.Brand : TuiToken.Ok;
        var label = DisplayLabel(display);
        var glyph = status switch
        {
            ToolLineStatus.Failed => "✗",
            ToolLineStatus.Running => "◐",
            ToolLineStatus.Unknown => "·",
            _ => "✓",
        };
        var metrics = status == ToolLineStatus.Failed
            ? string.IsNullOrWhiteSpace(meta) ? "failed" : "failed · " + meta
            : status == ToolLineStatus.Unknown ? "outcome unavailable"
            : meta ?? string.Empty;
        var head = glyph + " " + label;
        if (TuiText.VisibleWidth(head) + TuiText.VisibleWidth(metrics) + 3 > w)
            metrics = status == ToolLineStatus.Failed ? "failed" : string.Empty;
        var rightWidth = TuiText.VisibleWidth(metrics);
        var leftRoom = Math.Max(1, w - rightWidth - (rightWidth > 0 ? 3 : 0));
        head = TuiText.Truncate(head, leftRoom);
        var targetRoom = leftRoom - TuiText.VisibleWidth(head) - 2;
        var text = Paint(head, token);
        if (!string.IsNullOrWhiteSpace(target) && targetRoom > 2)
            text += "  " + Paint(TuiText.MiddleTruncate(ChatDisplayText.Sanitize(target, 512), targetRoom), TuiToken.Meta);
        if (rightWidth > 0)
            text += new string(' ', Math.Max(1, w - TuiText.VisibleWidth(text) - rightWidth))
                + Paint(metrics, status == ToolLineStatus.Failed ? TuiToken.Err : TuiToken.Line);
        return TuiText.Truncate(text, w);
    }

    /// <summary>Icon for a provider tool display name (OpenCode's glyph vocabulary).</summary>
    public static string Icon(string display) => DisplayLabel(display) switch
    {
        "View" or "Read" or "Docs" => "→",
        "Explore" or "Search" => "✱",
        "Edit" or "Write" or "Delete" => "←",
        "Terminal" or "Run" => "$",
        "Fetch" => "%",
        _ => "⚙",
    };

    private static string DisplayLabel(string display) => display.Trim().ToLowerInvariant() switch
    {
        "read_file" or "read" or "view" => "Read",
        "edit_file" or "apply_patch" or "edit" => "Edit",
        "write_file" or "write" => "Write",
        "bash" or "terminal" => "Run",
        "web_fetch" or "fetch" => "Fetch",
        "web_search" or "grep" or "search" => "Search",
        "todo" or "plan" => "Plan",
        _ => ChatDisplayText.Sanitize(display, 64),
    };
}
