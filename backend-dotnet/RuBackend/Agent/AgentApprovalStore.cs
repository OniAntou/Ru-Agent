using System.Collections.Concurrent;

namespace RuBackend.Agent;

public sealed class AgentApprovalStore
{
    private readonly ConcurrentDictionary<string, AgentApprovalRequest> _requests = new(StringComparer.Ordinal);

    public void Put(AgentApprovalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.ApprovalId)
            || string.IsNullOrWhiteSpace(request.SessionId)
            || string.IsNullOrWhiteSpace(request.ToolId)
            || string.IsNullOrWhiteSpace(request.SafeAction)
            || request.ApprovalId.Length > 128
            || request.SafeAction.Length > 256
            || request.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ArgumentException("Approval metadata is invalid.", nameof(request));
        }

        _requests[request.ApprovalId] = request;
    }

    public bool TryTake(
        string approvalId,
        string sessionId,
        out AgentApprovalRequest? request,
        DateTimeOffset? now = null)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(approvalId)
            || string.IsNullOrWhiteSpace(sessionId)
            || !_requests.TryGetValue(approvalId, out var candidate)
            || candidate.ExpiresAt <= (now ?? DateTimeOffset.UtcNow)
            || !string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal))
        {
            return false;
        }

        if (!_requests.TryRemove(new KeyValuePair<string, AgentApprovalRequest>(approvalId, candidate)))
        {
            return false;
        }

        request = candidate;
        return true;
    }

    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        foreach (var pair in _requests)
        {
            if (string.Equals(pair.Value.SessionId, sessionId, StringComparison.Ordinal))
            {
                _requests.TryRemove(pair.Key, out _);
            }
        }
    }

    public void Clear() => _requests.Clear();
}
