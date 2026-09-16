using RuAgent.Harness.Core.Prompting;

namespace RuApp.Services;

/// <summary>One history row after normalization: what it is, and what to show.</summary>
/// <param name="Role">The persisted role, unchanged.</param>
/// <param name="Content">Display text, with product markers removed.</param>
/// <param name="Render">False for a machine envelope that must never read as chat.</param>
public sealed record SessionHistoryRow(string Role, string Content, bool Render);

/// <summary>
/// Projects a persisted session history into the rows the transcript should render.
///
/// Three rules live here rather than in the reload method: display markers are
/// stripped from what the user reads, a machine tool envelope is never rendered as
/// prose, and the live activity rows belong to the last persisted assistant only when
/// that assistant is the final message.
/// </summary>
public static class SessionHistoryProjection
{
    /// <summary>
    /// Normalizes raw persisted messages. The marker strip applies to assistant text;
    /// a user turn is shown exactly as stored.
    /// </summary>
    public static IReadOnlyList<SessionHistoryRow> Project(IEnumerable<(string Role, string Content)> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        var rows = new List<SessionHistoryRow>();
        foreach (var (role, content) in history)
        {
            bool isAssistant = string.Equals(role, "assistant", StringComparison.Ordinal);
            string display = isAssistant
                ? ChatDisplayMarkers.Strip(content)
                : content ?? string.Empty;
            // A machine tool envelope must never render as chat text (for example a
            // repeated call that failed the run).
            bool render = !(isAssistant && ModelToolSelectionParser.LooksLikeToolSelection(display));

            rows.Add(new SessionHistoryRow(role, display, render));
        }

        return rows;
    }

    /// <summary>Index of the last assistant row in the raw history, or -1.</summary>
    public static int LastAssistantIndex(IEnumerable<(string Role, string Content)> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        int index = -1;
        int position = 0;
        foreach (var (role, _) in history)
        {
            if (string.Equals(role, "assistant", StringComparison.Ordinal))
            {
                index = position;
            }

            position++;
        }

        return index;
    }

    /// <summary>
    /// Whether the live activity rows may ride on the last persisted assistant. They
    /// may only do so when that assistant is the history's final message; otherwise
    /// the turn has no persisted assistant yet (the tool call was denied or failed)
    /// and the rows need their own bubble to stay visible.
    /// </summary>
    public static bool AttachLiveToLastAssistant(int historyCount, int lastAssistantIndex, int liveActivityCount) =>
        ReloadActivitiesPolicy.AttachLiveToLastAssistant(historyCount, lastAssistantIndex, liveActivityCount);
}
