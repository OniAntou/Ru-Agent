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
        DateTimeOffset? now = null) =>
        TryGet(
            sessionId,
            new HashSet<string>([requiredScope], StringComparer.Ordinal),
            out grant,
            now);

    public bool TryGet(
        string sessionId,
        IReadOnlySet<string> requiredScopes,
        out AgentSessionGrant? grant,
        DateTimeOffset? now = null)
    {
        grant = null;
        if (string.IsNullOrWhiteSpace(sessionId)
            || requiredScopes is null
            || requiredScopes.Count == 0
            || requiredScopes.Any(string.IsNullOrWhiteSpace))
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

        if (!requiredScopes.All(candidate.Scopes.Contains))
        {
            return false;
        }

        grant = candidate;
        return true;
    }

    public bool Remove(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) && _grants.TryRemove(sessionId, out _);

    public void Clear() => _grants.Clear();
}
