namespace RuAgent.SessionInterchange;

/// <summary>
/// The wording every format adapter needs and none of them should own alone.
/// Also the one token-count wording every surface reads, so GUI panels and the
/// CLI status line never disagree on K vs k.
/// </summary>
public static class TranscriptText
{
    private const int TitleCharacters = 100;

    public static string FormatTokens(long tokens)
    {
        if (tokens < 1000)
        {
            return tokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (tokens >= 1_000_000)
        {
            return (tokens / 1_000_000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "M";
        }

        return (tokens / 1000d).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "K";
    }

    /// <summary>
    /// A session's name, taken from the first thing a person asked.
    /// </summary>
    /// <remarks>
    /// Every partner derives a session's display name this way — Claude Code's own picker
    /// does — because only some of them store a name at all, and the ones that do keep it
    /// beside the transcript rather than in it. Deriving it in one place keeps three
    /// adapters from drifting into three slightly different names for the same session.
    /// </remarks>
    public static string? TitleFrom(IReadOnlyList<TranscriptEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.Kind != TranscriptEntryKind.UserMessage || string.IsNullOrWhiteSpace(entry.Text))
            {
                continue;
            }

            var line = entry.Text.Trim().Split('\n')[0].Trim();
            return line.Length <= TitleCharacters ? line : line[..TitleCharacters];
        }

        return null;
    }
}
