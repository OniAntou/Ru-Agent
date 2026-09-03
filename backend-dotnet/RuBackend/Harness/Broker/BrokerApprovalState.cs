using System.Collections.Concurrent;

namespace RuBackend.Harness.Broker;

/// <summary>
/// Shared, factory-scoped approval state for the Broker execution authority:
/// maps Host approval ids (derived from Broker ids) back to the Broker
/// pending approval plus the exact intent key, and keeps one-time
/// authorization tokens issued by the Broker so a resumed proposal can
/// execute without a second ask. State is run-scoped and dies with the run.
/// </summary>
public sealed class BrokerApprovalState
{
    private const string KeySeparator = "|";

    private sealed class RunState
    {
        public readonly ConcurrentDictionary<string, (string BrokerApprovalId, string ArgsKey)> Asks =
            new(StringComparer.Ordinal);

        public readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> Tokens =
            new(StringComparer.Ordinal);
    }

    private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);

    public void RegisterAsk(string runId, string hostApprovalId, string brokerApprovalId, string argsKey)
    {
        Run(runId).Asks[hostApprovalId] = (brokerApprovalId, argsKey);
    }

    public bool TryGetAsk(
        string runId,
        string hostApprovalId,
        out string brokerApprovalId,
        out string argsKey)
    {
        brokerApprovalId = string.Empty;
        argsKey = string.Empty;
        if (_runs.TryGetValue(runId, out var run)
            && run.Asks.TryGetValue(hostApprovalId, out var ask))
        {
            brokerApprovalId = ask.BrokerApprovalId;
            argsKey = ask.ArgsKey;
            return true;
        }

        return false;
    }

    public void StoreAuthorizationToken(string runId, string argsKey, string token, DateTimeOffset expiresAt)
    {
        Run(runId).Tokens[argsKey] = (token, expiresAt);
    }

    public bool TryPeekAuthorizationToken(string runId, string argsKey, out string? token)
    {
        token = null;
        if (!_runs.TryGetValue(runId, out var run)
            || !run.Tokens.TryGetValue(argsKey, out var entry)
            || entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        token = entry.Token;
        return true;
    }

    public bool TryConsumeAuthorizationToken(string runId, string argsKey, out string? token)
    {
        token = null;
        if (!_runs.TryGetValue(runId, out var run)
            || !run.Tokens.TryRemove(argsKey, out var entry)
            || entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        token = entry.Token;
        return true;
    }

    public void ClearRun(string runId)
    {
        _runs.TryRemove(runId, out _);
    }

    public void Clear() => _runs.Clear();

    private RunState Run(string runId) => _runs.GetOrAdd(runId, _ => new RunState());
}
