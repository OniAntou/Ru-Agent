using RuAgent.Client;

namespace RuApp.Services;

internal sealed class AgentSessionRecovery
{
    private readonly object _sync = new();
    private readonly HashSet<string> _attemptedSessions = new(StringComparer.Ordinal);

    public bool TryBegin(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (_sync)
        {
            return _attemptedSessions.Add(sessionId.Trim());
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _attemptedSessions.Clear();
        }
    }

    public static bool IsRecoverable(AgentApiException exception) =>
        exception is not null
        && exception.ErrorCode is "session_not_found" or "invalid_session_id";

    internal static async Task<bool?> TryRecoverAsync(
        AgentApiException error,
        bool enabled,
        string? sessionId,
        Func<bool> ownsSession,
        Func<bool> tryClaim,
        Func<Task> recover,
        Func<Task<bool>> replay)
    {
        ArgumentNullException.ThrowIfNull(ownsSession);
        ArgumentNullException.ThrowIfNull(tryClaim);
        ArgumentNullException.ThrowIfNull(recover);
        ArgumentNullException.ThrowIfNull(replay);
        if (!enabled || sessionId is null || !IsRecoverable(error) || !ownsSession() || !tryClaim())
        {
            return null;
        }

        await recover().ConfigureAwait(true);
        return await replay().ConfigureAwait(true);
    }
}
