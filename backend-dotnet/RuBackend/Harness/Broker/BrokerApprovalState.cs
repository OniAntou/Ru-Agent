namespace RuBackend.Harness.Broker;

/// <summary>
/// Shared, factory-scoped approval state for the Broker execution authority:
/// maps Host approval ids (derived from Broker ids) back to the Broker
/// pending approval plus the exact intent key, and keeps one-time
/// authorization tokens issued by the Broker so a resumed proposal can
/// execute without a second ask. State is run-scoped and dies with the run.
/// </summary>
public sealed record BrokerRunLeaseIdentity(
    string RunId,
    string SessionId,
    string WorkspaceId,
    string CanonicalRoot,
    long PolicyVersion,
    string PolicySha256,
    string LeaseId,
    string BrokerUrl,
    string AuthTokenSha256,
    long Generation = 0);

public sealed class BrokerApprovalState
{
    private const int MaxAuthorizationTokenChars = 4_096;

    private sealed record AskEntry(
        string BrokerApprovalId,
        string ArgsKey,
        BrokerRunLeaseIdentity Lease);

    private sealed record TokenEntry(
        string Token,
        DateTimeOffset ExpiresAt,
        BrokerRunLeaseIdentity Lease);

    private sealed record TokenKey(BrokerRunLeaseIdentity Lease, string ArgsKey);

    private sealed class RunState
    {
        public readonly Dictionary<string, AskEntry> Asks =
            new(StringComparer.Ordinal);

        public readonly Dictionary<TokenKey, TokenEntry> Tokens = new();
        public BrokerRunLeaseIdentity? ActiveLease;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, RunState> _runs = new(StringComparer.Ordinal);

    public void ActivateLease(BrokerRunLeaseIdentity lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateRunLease(lease.RunId, lease);
        lock (_sync)
        {
            var run = Run(lease.RunId);
            if (run.ActiveLease == lease)
            {
                return;
            }

            if (run.ActiveLease is { } previous)
            {
                if (SameLeaseScope(previous, lease))
                {
                    RemoveLeaseEntries(run, previous);
                }
                else
                {
                    run.Asks.Clear();
                    run.Tokens.Clear();
                }
            }

            run.ActiveLease = lease;
        }
    }

    public void InvalidateLease(BrokerRunLeaseIdentity lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_sync)
        {
            if (!_runs.TryGetValue(lease.RunId, out var run)
                || run.ActiveLease != lease)
            {
                return;
            }

            run.ActiveLease = null;
            RemoveLeaseEntries(run, lease);
        }
    }

    public bool RegisterAsk(
        string runId,
        BrokerRunLeaseIdentity lease,
        string hostApprovalId,
        string brokerApprovalId,
        string argsKey)
    {
        ValidateRunLease(runId, lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostApprovalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(brokerApprovalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(argsKey);
        lock (_sync)
        {
            if (!_runs.TryGetValue(runId, out var run)
                || run.ActiveLease != lease)
            {
                return false;
            }

            run.Asks[hostApprovalId] = new AskEntry(brokerApprovalId, argsKey, lease);
            return true;
        }
    }

    public bool TryGetAsk(
        string runId,
        BrokerRunLeaseIdentity lease,
        string hostApprovalId,
        out string brokerApprovalId,
        out string argsKey)
    {
        ValidateRunLease(runId, lease);
        brokerApprovalId = string.Empty;
        argsKey = string.Empty;
        lock (_sync)
        {
            if (_runs.TryGetValue(runId, out var run)
                && run.ActiveLease == lease
                && run.Asks.TryGetValue(hostApprovalId, out var ask)
                && ask.Lease == lease)
            {
                brokerApprovalId = ask.BrokerApprovalId;
                argsKey = ask.ArgsKey;
                return true;
            }

            return false;
        }
    }

    public void RemoveAsk(
        string runId,
        BrokerRunLeaseIdentity lease,
        string hostApprovalId)
    {
        ValidateRunLease(runId, lease);
        lock (_sync)
        {
            if (_runs.TryGetValue(runId, out var run)
                && run.ActiveLease == lease
                && run.Asks.TryGetValue(hostApprovalId, out var ask)
                && ask.Lease == lease)
            {
                run.Asks.Remove(hostApprovalId);
            }
        }
    }

    public bool StoreAuthorizationToken(
        string runId,
        BrokerRunLeaseIdentity lease,
        string argsKey,
        string token,
        DateTimeOffset expiresAt)
    {
        ValidateRunLease(runId, lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(argsKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length > MaxAuthorizationTokenChars)
        {
            throw new ArgumentOutOfRangeException(nameof(token), "Authorization token exceeds the size limit.");
        }

        lock (_sync)
        {
            if (!_runs.TryGetValue(runId, out var run)
                || run.ActiveLease != lease)
            {
                return false;
            }

            run.Tokens[new TokenKey(lease, argsKey)] = new TokenEntry(token, expiresAt, lease);
            return true;
        }
    }

    public bool TryTakeAuthorizationToken(
        string runId,
        BrokerRunLeaseIdentity lease,
        string argsKey,
        out string? token)
    {
        token = null;
        ValidateRunLease(runId, lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(argsKey);
        lock (_sync)
        {
            if (!_runs.TryGetValue(runId, out var run)
                || run.ActiveLease != lease)
            {
                return false;
            }

            var key = new TokenKey(lease, argsKey);
            if (!run.Tokens.TryGetValue(key, out var entry))
            {
                return false;
            }

            run.Tokens.Remove(key);
            if (string.IsNullOrWhiteSpace(entry.Token)
                || entry.Token.Length > MaxAuthorizationTokenChars
                || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            token = entry.Token;
            return true;
        }
    }

    public void InvalidateRunTokens(string runId)
    {
        lock (_sync)
        {
            if (_runs.TryGetValue(runId, out var run))
            {
                run.Tokens.Clear();
            }
        }
    }

    public void ClearRun(string runId)
    {
        lock (_sync)
        {
            _runs.Remove(runId);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _runs.Clear();
        }
    }

    private static void RemoveLeaseEntries(RunState run, BrokerRunLeaseIdentity lease)
    {
        foreach (var pair in run.Asks.ToArray())
        {
            if (SameLeaseScope(pair.Value.Lease, lease))
            {
                run.Asks.Remove(pair.Key);
            }
        }

        foreach (var pair in run.Tokens.ToArray())
        {
            if (pair.Key.Lease == lease)
            {
                run.Tokens.Remove(pair.Key);
            }
        }
    }

    private static bool SameLeaseScope(
        BrokerRunLeaseIdentity left,
        BrokerRunLeaseIdentity right) =>
        string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
        && string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal)
        && string.Equals(left.WorkspaceId, right.WorkspaceId, StringComparison.Ordinal)
        && string.Equals(left.CanonicalRoot, right.CanonicalRoot, StringComparison.Ordinal)
        && left.PolicyVersion == right.PolicyVersion
        && string.Equals(left.PolicySha256, right.PolicySha256, StringComparison.Ordinal)
        && string.Equals(left.LeaseId, right.LeaseId, StringComparison.Ordinal)
        && string.Equals(left.BrokerUrl, right.BrokerUrl, StringComparison.Ordinal)
        && string.Equals(left.AuthTokenSha256, right.AuthTokenSha256, StringComparison.Ordinal);

    private static void ValidateRunLease(string runId, BrokerRunLeaseIdentity lease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(lease);
        if (!string.Equals(runId, lease.RunId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The lease does not belong to the run.", nameof(lease));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(lease.LeaseId);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.PolicySha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(lease.BrokerUrl);
        if ((lease.AuthTokenSha256.Length != 64)
            || !lease.AuthTokenSha256.All(Uri.IsHexDigit)
            || !Uri.TryCreate(lease.BrokerUrl, UriKind.Absolute, out var brokerUri)
            || brokerUri.Scheme is not ("ws" or "wss"))
        {
            throw new ArgumentException("The Broker descriptor identity is invalid.", nameof(lease));
        }
    }

    private RunState Run(string runId)
    {
        if (!_runs.TryGetValue(runId, out var run))
        {
            run = new RunState();
            _runs[runId] = run;
        }

        return run;
    }
}
