namespace RuApp.Services;

public enum AgentNotificationKind
{
    Finished,
    Stopped,
    NeedsApproval,
    NeedsAnswer,
}

/// <summary>One notification to deliver. Text is generic: it may show on the lock screen.</summary>
public sealed record AgentNotification(AgentNotificationKind Kind, string SessionId, string Title, string Body);

/// <summary>
/// Decides when a run deserves a notification. Pure, so the replay rules are testable:
/// <list type="bullet">
/// <item>A finish is a live transition of one run from a non-terminal status to a terminal
/// one. Adopting or replaying an already finished run never notifies, and a run the user
/// cancelled does not either.</item>
/// <item>An approval or a question notifies once per id. Reconnects and replays deliver the
/// same id again and stay silent.</item>
/// <item>A background session (no live feed here) notifies when the session list sees it
/// start waiting on the user, or go from running or waiting to idle.</item>
/// </list>
/// Whether Ru is in the foreground, and which events the user enabled, is decided by the
/// caller; the policy only says what happened.
/// </summary>
public sealed class AgentNotificationPolicy
{
    private const int MaxTitleChars = 60;
    private readonly HashSet<string> _seenWaits = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BackgroundState> _backgroundRunning = new(StringComparer.Ordinal);
    private string? _runId;
    private string? _runStatus;

    /// <summary>
    /// The open session's run changed. Returns a notification for a live finish of the
    /// same run; a new run id only records its starting status.
    /// </summary>
    public AgentNotification? ObserveRun(string? sessionId, string? sessionTitle, string? runId, string? status)
    {
        bool sameRun = runId is not null && string.Equals(runId, _runId, StringComparison.Ordinal);
        bool wasLive = sameRun && !IsTerminal(_runStatus);
        _runId = runId;
        _runStatus = status;
        if (!wasLive || !IsTerminal(status) || string.IsNullOrWhiteSpace(sessionId)) return null;

        var title = Shorten(sessionTitle);
        return status switch
        {
            "completed" => new(AgentNotificationKind.Finished, sessionId, "Ru finished", title),
            "failed" or "interrupted" => new(AgentNotificationKind.Stopped, sessionId, "Ru stopped before finishing", title),
            // The user pressed Stop; telling them about it would be noise.
            _ => null,
        };
    }

    /// <summary>A pending approval or question appeared on the open session's run.</summary>
    public AgentNotification? ObserveWait(string? sessionId, string? sessionTitle, string? approvalId, string? inputId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        if (approvalId is not null && _seenWaits.Add("approval:" + approvalId))
            return new(AgentNotificationKind.NeedsApproval, sessionId, "Ru needs your approval", Shorten(sessionTitle));
        if (inputId is not null && _seenWaits.Add("input:" + inputId))
            return new(AgentNotificationKind.NeedsAnswer, sessionId, "Ru has a question for you", Shorten(sessionTitle));
        return null;
    }

    /// <summary>
    /// Marks waits that were already pending when a session was opened or a run adopted, so
    /// their replay does not notify.
    /// </summary>
    public void MarkSeen(string? approvalId, string? inputId)
    {
        if (approvalId is not null) _seenWaits.Add("approval:" + approvalId);
        if (inputId is not null) _seenWaits.Add("input:" + inputId);
    }

    /// <summary>
    /// The session list reported a session's run state: working (<paramref name="isRunning"/>)
    /// or paused on the user (<paramref name="wait"/>: "approval", "question",
    /// "workspace_trust" or "user"). The first report of a session is a baseline. Later, a new wait
    /// asks for attention, and a working-or-waiting to idle change is a finish the list
    /// noticed. A wait is still an unfinished run, so it never reads as a finish.
    /// </summary>
    public AgentNotification? ObserveBackgroundSession(string sessionId, string? sessionTitle, bool isRunning, string? wait = null)
    {
        bool known = _backgroundRunning.TryGetValue(sessionId, out var was);
        var now = new BackgroundState(isRunning || wait is not null, wait);
        _backgroundRunning[sessionId] = now;
        if (!known) return null;
        if (wait is not null && !string.Equals(wait, was.Wait, StringComparison.Ordinal))
        {
            return wait switch
            {
                "question" => new(AgentNotificationKind.NeedsAnswer, sessionId, "Ru has a question for you", Shorten(sessionTitle)),
                "approval" or "workspace_trust" => new(AgentNotificationKind.NeedsApproval, sessionId, "Ru needs your approval", Shorten(sessionTitle)),
                _ => new(AgentNotificationKind.NeedsAnswer, sessionId, "Ru is waiting for you", Shorten(sessionTitle)),
            };
        }
        return was.Active && !now.Active
            ? new(AgentNotificationKind.Finished, sessionId, "Ru finished", Shorten(sessionTitle))
            : null;
    }

    private readonly record struct BackgroundState(bool Active, string? Wait);

    public static bool IsTerminal(string? status) => status is "completed" or "failed" or "cancelled" or "interrupted";

    private static string Shorten(string? title)
    {
        var text = string.IsNullOrWhiteSpace(title) ? "Session" : title.Trim();
        return text.Length <= MaxTitleChars ? text : TextTruncation.TakeAtMost(text, MaxTitleChars - 1) + "…";
    }
}
