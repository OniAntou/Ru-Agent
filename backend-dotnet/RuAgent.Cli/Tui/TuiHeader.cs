using System.Globalization;
using System.Text;

namespace RuAgent.Cli.Tui;

/// <summary>
/// The pinned session header and the pre-chat home surface, cloned from OpenCode: a one-row
/// header with the session title and token tally, and a landing screen with a centered
/// two-tone block wordmark.
/// </summary>
public static class TuiHeader
{
    /// <summary>
    /// One header row: "# &lt;title&gt;" at the left edge and the usage cluster (grouped token
    /// count, context percentage, session cost) at the right edge. The title is shortened
    /// first; the cluster is only dropped when the row can no longer fit both.
    /// </summary>
    public static string Compose(
        int width,
        string? title,
        long? tokens,
        int? contextPercent = null,
        double? costUsd = null)
    {
        if (width <= 0)
        {
            return string.Empty;
        }

        var parts = new List<string>(3);
        if (tokens is > 0)
        {
            parts.Add(tokens.Value.ToString("N0", CultureInfo.InvariantCulture));
        }

        if (contextPercent is { } percent)
        {
            parts.Add($"{percent}%");
        }

        if (costUsd is { } cost)
        {
            parts.Add($"(${cost.ToString("0.00", CultureInfo.InvariantCulture)})");
        }

        var tally = parts.Count == 0
            ? string.Empty
            : TuiPalette.Paint(string.Join("  ", parts), TuiToken.Meta);
        var tallyWidth = TuiText.VisibleWidth(tally);
        var titleBudget = tallyWidth > 0 ? width - tallyWidth - 2 : width;
        var heading = string.IsNullOrWhiteSpace(title) ? "session" : title.Trim();
        var left = TuiPalette.Paint("# ", TuiToken.Bright) + TuiPalette.Paint(heading, TuiToken.Bright);
        if (TuiText.VisibleWidth(left) > titleBudget)
        {
            left = TuiText.Truncate(left, Math.Max(0, titleBudget));
        }

        return tallyWidth > 0 && titleBudget >= 2
            ? TwoColumn(left, tally, width)
            : TuiText.Pad(TuiText.Truncate(left, width), width);
    }

    /// <summary>
    /// Paints the pinned full-screen header into <paramref name="height"/> absolute rows
    /// starting at <paramref name="topRow"/> (1-based), then parks the cursor on the first row
    /// below it so a sequential write continues into the transcript region. Each row is erased
    /// first: a resize rebuild can otherwise leave old text of a different width behind.
    /// </summary>
    public static void RenderPinned(
        TuiSurface surface,
        int width,
        int topRow,
        string? title,
        long? tokens,
        int height,
        int? contextPercent = null,
        double? costUsd = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        var rows = Math.Max(0, height);
        var first = Math.Max(1, topRow);
        var lines = new[] { Compose(width, title, tokens, contextPercent, costUsd) };
        surface.Repaint(writer =>
        {
            var builder = new StringBuilder();
            for (var i = 0; i < rows && i < lines.Length; i++)
            {
                builder.Append($"\u001b[{first + i};1H");
                builder.Append("\u001b[2K");
                builder.Append(TuiText.Truncate(lines[i], width));
            }

            builder.Append($"\u001b[{first + rows};1H");
            writer.Write(builder.ToString());
        });
    }

    /// <summary>Rows reserved below the landing block for the composer frame.</summary>
    public const int ComposerReserveRows = 1 + ComposerBox.StackHeight + 1;

    /// <summary>Rows of the block wordmark.</summary>
    public const int WordmarkRows = 4;

    /// <summary>Narrowest terminal that fits the two-tone block wordmark.</summary>
    public const int MinWordmarkWidth = 35;

    /// <summary>
    /// The pre-chat home surface, cloned from OpenCode: the centered block wordmark with
    /// blank space above and below; the composer lives in the pinned footer below it. When
    /// <paramref name="viewportRows"/> has room to spare the block sits vertically centered.
    /// </summary>
    public static IReadOnlyList<string> ComposeLandingScreen(int width, int viewportRows = 0)
    {
        if (width <= 0)
        {
            return Array.Empty<string>();
        }

        var w = Math.Clamp(width, 20, 1000);
        var content = Wordmark(w);
        var lines = new List<string>();
        if (viewportRows > content.Count + ComposerReserveRows)
        {
            var target = viewportRows - ComposerReserveRows;
            var topPad = (target - content.Count) / 2;
            var bottomPad = target - content.Count - topPad;
            for (var i = 0; i < topPad; i++)
            {
                lines.Add(new string(' ', w));
            }

            lines.AddRange(content);
            for (var i = 0; i < bottomPad; i++)
            {
                lines.Add(new string(' ', w));
            }

            return lines;
        }

        lines.AddRange(content);
        return lines;
    }

    /// <summary>
    /// The two-tone block wordmark: "ru" in the muted tone and "agent" bright, matching
    /// OpenCode's dim-left/bright-right mark. Falls back to plain text on narrow terminals.
    /// </summary>
    public static IReadOnlyList<string> Wordmark(int width)
    {
        if (width <= 0)
        {
            return Array.Empty<string>();
        }

        var w = Math.Clamp(width, 20, 1000);
        var lines = new List<string> { string.Empty };
        if (w < MinWordmarkWidth)
        {
            lines.Add(Center(TuiPalette.Paint("Ru Agent", TuiToken.Bright), w));
            lines.Add(string.Empty);
            return lines;
        }

        for (var i = 0; i < WordmarkRows; i++)
        {
            var row = TuiPalette.Paint(RuWord[i], TuiToken.Meta)
                + " "
                + TuiPalette.Paint(AgentWord[i], TuiToken.Bright);
            lines.Add(Center(row, w));
        }

        lines.Add(string.Empty);
        return lines;
    }

    private static readonly string[] RuWord =
    [
        "█▀▀█ █  █",
        "█▀▀▄ █  █",
        "█  █ █  █",
        "▀  ▀ ▀▀▀▀",
    ];

    private static readonly string[] AgentWord =
    [
        "█▀▀█ █▀▀▀ █▀▀▀ █  █ ▀█▀",
        "█▀▀█ █ ▀▀ █▀▀  █▄ █  █ ",
        "█  █ █  █ █    █ ▀█  █ ",
        "▀  ▀ ▀▀▀▀ ▀▀▀▀ ▀  ▀  ▀ ",
    ];

    /// <summary>The muted "ru" half of the block wordmark, one string per row.</summary>
    public static IReadOnlyList<string> WordmarkRuRows => RuWord;

    /// <summary>The bright "agent" half of the block wordmark, one string per row.</summary>
    public static IReadOnlyList<string> WordmarkAgentRows => AgentWord;

    private static string Center(string text, int width)
    {
        var visible = TuiText.VisibleWidth(text);
        if (visible >= width)
        {
            return TuiText.Truncate(text, width);
        }

        var left = (width - visible) / 2;
        return new string(' ', left) + text + new string(' ', width - visible - left);
    }

    private static string TwoColumn(string left, string right, int width)
    {
        if (right.Length == 0)
        {
            return TuiText.Pad(left, width);
        }

        var leftWidth = TuiText.VisibleWidth(left);
        var rightWidth = TuiText.VisibleWidth(right);
        var room = width - leftWidth - rightWidth;
        if (room < 2)
        {
            return TuiText.Pad(left, width);
        }

        return left + new string(' ', room) + right;
    }

    /// <summary>Keeps the tail of a path so the leaf stays visible on narrow terminals.</summary>
    public static string ShortenWorkspace(string path, int maxCells)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var normalized = path.Replace('\\', '/').TrimEnd('/');
        if (TuiText.VisibleWidth(normalized) <= maxCells)
        {
            return normalized;
        }

        var segments = normalized.Split('/');
        var built = string.Empty;
        for (var i = segments.Length - 1; i >= 0; i--)
        {
            var candidate = segments[i] + (built.Length == 0 ? string.Empty : "/" + built);
            if (TuiText.VisibleWidth("…/" + candidate) > maxCells)
            {
                break;
            }

            built = candidate;
        }

        return built.Length == 0 ? TuiText.Truncate(normalized, maxCells) : "…/" + built;
    }
}
