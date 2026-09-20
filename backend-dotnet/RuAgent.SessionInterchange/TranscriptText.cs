namespace RuAgent.SessionInterchange;

/// <summary>
/// The wording every format adapter needs and none of them should own alone.
/// </summary>
internal static class TranscriptText
{
    private const int TitleCharacters = 100;

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
