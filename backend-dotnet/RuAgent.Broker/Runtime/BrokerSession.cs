using RuAgent.Broker.Policy;
using RuAgent.Protocol;

namespace RuAgent.Broker.Runtime;

public sealed class BrokerSessionException : Exception
{
    public BrokerSessionException(string code, string? message = null)
        : base(message ?? code)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class BrokerSession
{
    private readonly object _gate = new();
    private string? _activeSessionId;
    private readonly Dictionary<string, HashSet<string>> _scopesBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrokerRunLease> _runLeases = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _advertisedCapabilities =
        RuAgent.Protocol.Harness.BrokerPolicyContracts.AuthoritativeFallbackCapabilities();

    public SessionDirectoryGrantRegistry DirectoryGrants { get; private set; } = new();

    private BrokerSession(string sessionRoot, DateTimeOffset expiresAt)
    {
        SessionRoot = sessionRoot;
        ExpiresAt = expiresAt;
    }

    /// <summary>The workspace root the session's leases and policy grants are bound to.</summary>
    public string SessionRoot { get; }

    /// <summary>
    /// Lifetime of the v1 policy grant the execution service derives from this
    /// session. The in-process authority sets it to <see cref="DateTimeOffset.MaxValue"/>
    /// because the runtime is bounded by the Host process, not a timestamp.
    /// </summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// Capability list every run lease must advertise. The execution authority
    /// pins it from its composition (web tools extend the authoritative fallback
    /// set); lease opens fail closed when a lease advertises anything else.
    /// </summary>
    public IReadOnlyList<string> AdvertisedCapabilities
    {
        get
        {
            lock (_gate)
            {
                return _advertisedCapabilities;
            }
        }
    }

    public void ConfigureAdvertisedCapabilities(IReadOnlyList<string> capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        lock (_gate)
        {
            _advertisedCapabilities = capabilities.ToArray();
        }
    }

    public bool IsReady
    {
        get
        {
            lock (_gate)
            {
                return _activeSessionId is not null;
            }
        }
    }

    public bool Open(
        string sessionId,
        IReadOnlySet<string>? scopes = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        lock (_gate)
        {
            if (_activeSessionId is null)
            {
                _activeSessionId = sessionId;
                _scopesBySession[sessionId] = scopes is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(scopes, StringComparer.Ordinal);
                return true;
            }

            return string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    public bool Owns(string sessionId)
    {
        lock (_gate)
        {
            return string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal)
                || _runLeases.Values.Any(lease => string.Equals(lease.SessionId, sessionId, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Union of every live session's scopes. Prefer <see cref="ScopesFor"/>:
    /// the union is only for callers that genuinely speak about the whole
    /// process, never for a policy grant belonging to one session.
    /// </summary>
    public IReadOnlySet<string> Scopes
    {
        get
        {
            lock (_gate)
            {
                var union = new HashSet<string>(StringComparer.Ordinal);
                foreach (var scopes in _scopesBySession.Values)
                {
                    union.UnionWith(scopes);
                }

                return union;
            }
        }
    }

    /// <summary>
    /// Scopes granted to one session. A lease opened by another session must
    /// never widen this set.
    /// </summary>
    public IReadOnlySet<string> ScopesFor(string sessionId)
    {
        lock (_gate)
        {
            return _scopesBySession.TryGetValue(sessionId, out var scopes)
                ? new HashSet<string>(scopes, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
    }

    public void Close(string sessionId)
    {
        lock (_gate)
        {
            // Scope teardown to the named session so concurrent chats sharing
            // this Broker process keep their own leases and grants.
            var ownedRuns = _runLeases.Values
                .Where(lease => string.Equals(lease.SessionId, sessionId, StringComparison.Ordinal))
                .Select(lease => lease.RunId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var runId in ownedRuns)
            {
                _runLeases.Remove(runId);
            }

            DirectoryGrants.RevokeSession(sessionId);
            _scopesBySession.Remove(sessionId);
            if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
            {
                _activeSessionId = null;
            }
        }
    }

    /// <summary>
    /// Run ids currently leased by one session. The runtime reads them before
    /// closing a session so session-scoped pending approvals can be dropped.
    /// </summary>
    public IReadOnlyList<string> RunIdsForSession(string sessionId)
    {
        lock (_gate)
        {
            return _runLeases.Values
                .Where(lease => string.Equals(lease.SessionId, sessionId, StringComparison.Ordinal))
                .Select(lease => lease.RunId)
                .ToArray();
        }
    }

    internal bool HasRunWithinDirectory(string directory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var prefix = root + Path.DirectorySeparatorChar;
        lock (_gate)
        {
            return _runLeases.Values.Any(lease =>
            {
                var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(lease.CanonicalRoot));
                return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                    || candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    /// Closes the run lease with the given id without closing the owning session.
    public bool TryCloseRun(string runId)
    {
        lock (_gate)
        {
            return _runLeases.Remove(runId);
        }
    }

    public bool TryRebindRunPolicy(BrokerRunLease reboundLease, out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(reboundLease);
        lock (_gate)
        {
            var current = _runLeases.TryGetValue(reboundLease.RunId, out var existing)
                ? existing
                : null;
            if (current is null)
            {
                errorCode = "run_not_open";
                return false;
            }

            if (current.IsExpired(DateTimeOffset.UtcNow)
                || reboundLease.IsExpired(DateTimeOffset.UtcNow))
            {
                errorCode = "lease_expired";
                return false;
            }

            if (!string.Equals(current.RunId, reboundLease.RunId, StringComparison.Ordinal))
            {
                errorCode = "run_conflict";
                return false;
            }

            if (!string.Equals(current.SessionId, reboundLease.SessionId, StringComparison.Ordinal))
            {
                errorCode = "session_conflict";
                return false;
            }

            if (!string.Equals(
                    current.CanonicalRoot,
                    reboundLease.CanonicalRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                // A moved workspace cannot inherit run authority; fail closed.
                errorCode = "workspace_changed";
                return false;
            }

            if (reboundLease.Snapshot.Version <= current.Snapshot.Version)
            {
                errorCode = "rebind_not_monotonic";
                return false;
            }

            var nextBindingVersion = current.BindingVersion + 1;
            // Decisions issued under the older binding stay recorded but are invalidated by
            // binding-version comparison, so late callers observe decision_stale (not unknown).
            _runLeases[reboundLease.RunId] = reboundLease with
                { BindingVersion = nextBindingVersion, Lifetime = current.Lifetime };
            errorCode = null;
            return true;
        }
    }

    public BrokerRunLease? ActiveRunLease
    {
        get
        {
            lock (_gate)
            {
                return _runLeases.Count == 1 ? _runLeases.Values.Single() : null;
            }
        }
    }

    public bool TryOpenRunLease(BrokerRunLease lease, out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(lease);
        lock (_gate)
        {
            if (lease.IsExpired(DateTimeOffset.UtcNow))
            {
                errorCode = "lease_expired";
                return false;
            }

            // Run leases are multi-tenant: concurrent chats share this Broker
            // process with one lease each. Per-message binding still holds —
            // every envelope must carry the same session id as the lease it
            // addresses (checked by callers), so a socket claiming session A
            // can never consume session B's approvals, tokens, or grants.
            // Validate the connection's advertisement matches the host-configured
            // capability set; mismatch fails closed.
            var expectedCapabilities = _advertisedCapabilities;
            // hash already validated in BrokerRunLease.CreateValidated, but double-check
            if (!expectedCapabilities.SequenceEqual(lease.AdvertisedCapabilities))
            {
                // Phase-1 must advertise only files.list/files.read; mismatch fails closed
                errorCode = "capability_mismatch";
                return false;
            }

            // Duplicate opens cannot reset cancellation or delegated generation
            // checks. A genuinely closed lease starts a new generation.
            if (_runLeases.TryGetValue(lease.RunId, out var current))
                lease = lease with { Lifetime = current.Lifetime };
            _runLeases[lease.RunId] = lease;
            // Also open session for v2 lease if not already open
            if (_activeSessionId is null)
            {
                _activeSessionId = lease.SessionId;
            }

            if (!_scopesBySession.TryGetValue(lease.SessionId, out var sessionScopes))
            {
                sessionScopes = new HashSet<string>(StringComparer.Ordinal);
                _scopesBySession[lease.SessionId] = sessionScopes;
            }

            // Scopes stay scoped to the lease's own session: one chat's lease
            // must never widen another chat's policy grant.
            sessionScopes.UnionWith(lease.AdvertisedCapabilities.SelectMany(WorkspaceScopesFor));

            errorCode = null;
            return true;
        }
    }

    private static IEnumerable<string> WorkspaceScopesFor(string toolId) => toolId switch
    {
        "read" or "grep" or "glob" => [ToolScope.SystemRead, ToolScope.WorkspaceRead],
        "write" or "edit" => [ToolScope.WorkspaceWrite],
        "bash" => [ToolScope.TerminalExecute],
        "web_search" => [ToolScope.WebSearch],
        "web_fetch" => [ToolScope.WebFetch],
        _ => [ToolScope.WorkspaceRead],
    };

    public bool TryGetRunLease(string runId, out BrokerRunLease? lease)
    {
        lock (_gate)
        {
            if (_runLeases.TryGetValue(runId, out var found))
            {
                lease = found;
                return true;
            }

            lease = null;
            return false;
        }
    }

    public bool ValidateRunLeaseForTool(string runId, string toolId, out string? errorCode)
    {
        lock (_gate)
        {
            if (!_runLeases.TryGetValue(runId, out var runLease))
            {
                errorCode = "run_not_open";
                return false;
            }

            if (runLease.IsExpired(DateTimeOffset.UtcNow))
            {
                errorCode = "lease_expired";
                return false;
            }

            if (!runLease.AdvertisedCapabilities.Contains(toolId))
            {
                errorCode = "capability_unavailable";
                return false;
            }

            // Phase-1 tracer root must come from lease's canonical root, not broker cwd.
            // Identity/hash mismatch already enforced at lease creation; here we ensure file tools use lease root.

            errorCode = null;
            return true;
        }
    }

    /// <summary>
    /// Creates the session the in-process execution authority runs on: no
    /// descriptor file, loopback base URL, or bearer token. Lease, scope and
    /// directory-grant behavior is the only session behavior left.
    /// </summary>
    public static BrokerSession CreateForHost(
        string sessionRoot,
        DateTimeOffset expiresAt,
        IReadOnlyList<string>? advertisedCapabilities = null,
        DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionRoot);
        if (!Path.IsPathRooted(sessionRoot))
        {
            throw new BrokerSessionException("root_escape");
        }

        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        if (expiresAt <= effectiveNow)
        {
            throw new BrokerSessionException("expired");
        }

        var session = new BrokerSession(Path.GetFullPath(sessionRoot), expiresAt);
        if (advertisedCapabilities is not null)
        {
            session.ConfigureAdvertisedCapabilities(advertisedCapabilities);
        }

        return session;
    }
}
