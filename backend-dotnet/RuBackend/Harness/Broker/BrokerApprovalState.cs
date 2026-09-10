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
    private const int MaxAuthorizationTokenChars = 4_096;

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
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(argsKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length > MaxAuthorizationTokenChars)
        {
            throw new ArgumentOutOfRangeException(nameof(token), "Authorization token exceeds the size limit.");
        }

        Run(runId).Tokens[argsKey] = (token, expiresAt);
    }

    public bool TryPeekAuthorizationToken(string runId, string argsKey, out string? token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(argsKey))
        {
            return false;
        }

        if (!_runs.TryGetValue(runId, out var run)
            || !run.Tokens.TryGetValue(argsKey, out var entry)
            || string.IsNullOrWhiteSpace(entry.Token)
            || entry.Token.Length > MaxAuthorizationTokenChars
            || entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        token = entry.Token;
        return true;
    }

    /// <summary>
    /// Drops cached authorization tokens for one run. Call when the policy
    /// snapshot rotates (N+1 invalidates pending N decisions) so a token
    /// issued under the old snapshot cannot authorize the new one.
    /// </summary>
    public void InvalidateRunTokens(string runId)
    {
        if (_runs.TryGetValue(runId, out var run))
        {
            run.Tokens.Clear();
        }
    }

    public void ClearRun(string runId)
    {
        _runs.TryRemove(runId, out _);
    }

    public void Clear() => _runs.Clear();

    private RunState Run(string runId) => _runs.GetOrAdd(runId, _ => new RunState());
}
