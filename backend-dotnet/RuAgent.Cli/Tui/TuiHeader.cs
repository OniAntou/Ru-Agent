namespace RuAgent.Cli.Tui;

/// <summary>Shared logo resources and workspace text; native views own all drawing.</summary>
public static class TuiHeader
{
    public const int WordmarkRows = 3;
    public const int MinWordmarkWidth = 35;
    public const int MeteorFrameMilliseconds = 18;

    private static readonly string[] RuWord =
    [
        "█▀▀█ █  █",
        "█▀▀▄ █  █",
        "█  █ █▄▄█",
    ];

    /// <summary>
    /// The "agent" half, three block rows. G carries its crossbar on the lower half of the middle
    /// row rather than the upper one: with the crossbar up top and a solid base the letter read
    /// as a C with a bar through it, and a filled base on its own made it an O. Putting the
    /// crossbar low and closing only the two feet is what makes the letter a G, and it keeps G
    /// the same six half-cells tall as every other letter. E mirrors that logic on all three of its
    /// bars: the top and middle bars ride the upper half (<c>▀</c>) while the base rides the lower
    /// half (<c>▄</c>), so each bar sits on the inner edge of the row instead of floating or
    /// straddling it.
    /// </summary>
    private static readonly string[] AgentWord =
    [
        "█▀▀█ █▀▀▀ █▀▀▀ █  █ ▀█▀",
        "█▀▀█ █ ▄▄ █▀▀  █▄ █  █ ",
        "█  █ █▄▄█ █▄▄▄ █ ▀█  █ ",
    ];

    /// <summary>The "ru" half of the aurora block wordmark, one string per row.</summary>
    public static IReadOnlyList<string> WordmarkRuRows => RuWord;

    /// <summary>The "agent" half of the aurora block wordmark, one string per row.</summary>
    public static IReadOnlyList<string> WordmarkAgentRows => AgentWord;

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
