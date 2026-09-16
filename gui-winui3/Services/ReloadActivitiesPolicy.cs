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
}
