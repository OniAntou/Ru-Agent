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
}
