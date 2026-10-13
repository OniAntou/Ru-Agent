namespace RuApp.Services;

/// <summary>
/// Decides where the live activity rows go when the transcript is rebuilt
/// from session history at the end of a turn. The live collection belongs to
/// the turn that just ended; history may not have an assistant message for it
/// yet (deny or fail before any assistant text was persisted).
/// </summary>
internal static class ReloadActivitiesPolicy
{
    /// <summary>
    /// The live rows may only ride on history's last assistant when that
    /// assistant is the final persisted message, i.e. it belongs to the turn
    /// that just ended. Attaching them to an older bubble shows this turn's
    /// rows in the wrong place and leaves the current turn empty.
    /// </summary>
    public static bool AttachLiveToLastAssistant(int historyCount, int lastAssistantIndex, int liveCount) =>
        liveCount > 0 && lastAssistantIndex == historyCount - 1;

    /// <summary>
    /// When the live rows were not attached to any restored bubble, keep them
    /// in a trailing assistant bubble instead of dropping them with the
    /// placeholder. Without this a denied/failed turn loses its whole feed.
    /// </summary>
    public static bool AppendOrphanedActivities(int liveCount, bool liveAttached) =>
        liveCount > 0 && !liveAttached;

    /// <summary>
    /// Whether a persisted message becomes a visible assistant bubble.
    /// </summary>
    /// <remarks>
    /// A machine tool envelope is stored like any other assistant message but must never
    /// render as chat text. It is the reason the ordinal below counts rendered bubbles
    /// rather than persisted ones.
    /// </remarks>
    public static bool RendersAsAssistant(string? role, string? content, Func<string, bool> isToolSelection) =>
        string.Equals(role, "assistant", StringComparison.Ordinal)
        && !isToolSelection(content ?? string.Empty);

    /// <summary>
    /// What to render for one persisted message, and which run's rows back it.
    /// </summary>
    /// <param name="Renders">Whether the message becomes a visible assistant bubble.</param>
    /// <param name="ReplayIndex">The run backing it, or -1 when no run does.</param>
    public readonly record struct ReplayPlacement(bool Renders, int ReplayIndex);

    /// <summary>
    /// Legacy ordinal matching for a balanced message/run history only.
    /// </summary>
    /// <remarks>
    /// Modern histories use source_run_id through SessionTranscriptBuilder.
    /// Without identities, unequal counts cannot reveal which turn has no
    /// response. Retain the saved prose instead of guessing from the tail.
    ///
    /// Rendering and backing are answered separately and deliberately. Deriving one from
    /// the other is wrong in both directions: a bubble that renders may have no run behind
    /// it, and a hidden machine envelope may sit in front of runs it must not claim.
    /// </remarks>
    public static IReadOnlyList<ReplayPlacement> PlaceReplayedRuns(
        int runCount,
        IReadOnlyList<bool> assistantFlags)
    {
        ArgumentNullException.ThrowIfNull(assistantFlags);

        int totalAssistants = assistantFlags.Count(flag => flag);
        var placements = new List<ReplayPlacement>(assistantFlags.Count);
        int ordinal = 0;
        foreach (bool renders in assistantFlags)
        {
            if (!renders)
            {
                placements.Add(new ReplayPlacement(false, -1));
                continue;
            }

            placements.Add(new ReplayPlacement(
                true,
                ReplayIndexForAssistant(runCount, totalAssistants, ordinal)));
            ordinal++;
        }

        return placements;
    }

    /// <summary>
    /// Maps a legacy rendered assistant ordinal only when counts match.
    /// </summary>
    public static int ReplayIndexForAssistant(int runCount, int totalAssistants, int assistantOrdinal) =>
        runCount == totalAssistants && assistantOrdinal >= 0 && assistantOrdinal < runCount
            ? assistantOrdinal
            : -1;
}
