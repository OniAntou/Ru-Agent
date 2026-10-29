namespace RuAgent.Cli.Tui;

/// <summary>
/// The inline tool row: a state glyph, the tool display name, its target and right-aligned
/// metrics, indented as feed content rather than prose.
///
/// The hierarchy is deliberate: the glyph carries the outcome colour, the name is muted,
/// the target is the brightest element on the row, and the metrics recede. A row where
/// every cell has the same weight reads as a log; this one reads as a sentence.
/// Pure string-in/line-out so snapshot tests can pin the output.
/// </summary>
public static class TuiToolRow
{
    /// <summary>Narrowest row this renderer produces; requested widths below this clamp up.</summary>
    public const int MinWidth = 14;

    /// <summary>Feed rows sit two cells inside the transcript's text column.</summary>
    public const int Indent = 2;

    /// <summary>
    /// One settled tool row: <c>{indent}{glyph} {Display} {target} {metrics}</c>, right-aligned
    /// to <paramref name="width"/> cells. Metrics are composed by the transcript.
    /// </summary>
    public static string Compose(
        ToolLineStatus status, string display, string? target, string? meta, int width, int indent = Indent) =>
        TuiText.Truncate(
            string.Concat(Layout(status, display, target, meta, width, indent)
                .Select(segment => TuiPalette.Paint(segment.Text, segment.Token))),
            Math.Max(MinWidth, width));

    public static TranscriptLine ComposePresentation(
        ToolLineStatus status, string display, string? target, string? meta, int width, int indent = Indent)
    {
        var builder = new TranscriptRunBuilder();
        foreach (var segment in Layout(status, display, target, meta, width, indent))
        {
            builder.Append(segment.Text, segment.Token);
        }

        return builder.Line(TranscriptRole.Tool);
    }

    private static IReadOnlyList<(string Text, TuiToken Token)> Layout(
        ToolLineStatus status, string display, string? target, string? meta, int width, int indent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(display);
        var w = Math.Max(MinWidth, width);
        var indentWidth = Math.Clamp(indent, 0, Math.Max(0, w - 8));
        var usable = w - indentWidth;

        var statusToken = status switch
        {
            ToolLineStatus.Failed => TuiToken.Err,
            ToolLineStatus.Unknown => TuiToken.Meta,
            ToolLineStatus.Running => TuiToken.Brand,
            _ => TuiToken.Ok,
        };
        var glyph = status switch
        {
            ToolLineStatus.Failed => "✗",
            ToolLineStatus.Running => "◐",
            ToolLineStatus.Unknown => "·",
            _ => "✓",
        };

        var label = DisplayLabel(display);
        var metrics = status == ToolLineStatus.Failed
            ? string.IsNullOrWhiteSpace(meta) ? "failed" : "failed · " + meta
            : status == ToolLineStatus.Unknown ? "outcome unavailable"
            : meta ?? string.Empty;

        if (2 + TuiText.VisibleWidth(label) + TuiText.VisibleWidth(metrics) + 3 > usable)
        {
            metrics = status == ToolLineStatus.Failed ? "failed" : string.Empty;
        }

        var rightWidth = TuiText.VisibleWidth(metrics);
        var leftRoom = Math.Max(1, usable - rightWidth - (rightWidth > 0 ? 3 : 0));
        label = TuiText.Truncate(label, Math.Max(1, leftRoom - 2));
        var used = indentWidth + 2 + TuiText.VisibleWidth(label);

        var segments = new List<(string Text, TuiToken Token)>(8);
        if (indentWidth > 0)
        {
            segments.Add((new string(' ', indentWidth), TuiToken.Line));
        }

        segments.Add((glyph, statusToken));
        segments.Add((" ", TuiToken.Line));
        segments.Add((label, TuiToken.Meta));

        var targetRoom = leftRoom - 2 - TuiText.VisibleWidth(label) - 2;
        if (!string.IsNullOrWhiteSpace(target) && targetRoom > 2)
        {
            var clipped = TuiText.MiddleTruncate(ChatDisplayText.Sanitize(target, 512), targetRoom);
            if (clipped.Length > 0)
            {
                segments.Add(("  ", TuiToken.Line));
                segments.Add((clipped, TuiToken.Ink));
                used += 2 + TuiText.VisibleWidth(clipped);
            }
        }

        if (rightWidth > 0)
        {
            segments.Add((new string(' ', Math.Max(1, w - used - rightWidth)), TuiToken.Line));
            segments.Add((metrics, status == ToolLineStatus.Failed ? TuiToken.Err : TuiToken.Meta));
        }

        return segments;
    }

    /// <summary>Icon for a provider tool display name (OpenCode's glyph vocabulary).</summary>
    public static string Icon(string display) => DisplayLabel(display) switch
    {
        "View" or "Read" or "Docs" => "→",
        "Explore" or "Search" or "List" or "Glob" => "✱",
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
        "web_search" or "web search" or "grep" or "search" => "Search",
        "list" => "List",
        "glob" => "Glob",
        "todo" or "plan" => "Plan",
        _ => ChatDisplayText.Sanitize(display, 64),
    };
}
