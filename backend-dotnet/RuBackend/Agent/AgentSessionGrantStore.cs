using System.Collections.Concurrent;

namespace RuBackend.Agent;

public sealed class AgentSessionGrantStore
{
    private readonly ConcurrentDictionary<string, AgentSessionGrant> _grants = new(StringComparer.Ordinal);

    public void Put(AgentSessionGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        if (string.IsNullOrWhiteSpace(grant.SessionId))
        {
            throw new ArgumentException("Session ID is required.", nameof(grant));
        }

        _grants[grant.SessionId] = grant;
    }

    public bool TryGet(
        string sessionId,
        string requiredScope,
        out AgentSessionGrant? grant,
        DateTimeOffset? now = null)
    {
        grant = null;
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(requiredScope))
        {
            return false;
        }

        if (!_grants.TryGetValue(sessionId, out var candidate))
        {
            return false;
        }

        var current = now ?? DateTimeOffset.UtcNow;
        if (candidate.ExpiresAt <= current)
        {
            _grants.TryRemove(new KeyValuePair<string, AgentSessionGrant>(sessionId, candidate));
            return false;
        }

        if (!candidate.Scopes.Contains(requiredScope))
        {
            return false;
        }

        grant = candidate;
        return true;
    }

    public bool Remove(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _grants.TryRemove(sessionId, out _);
}
