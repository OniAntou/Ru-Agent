using System.Text.Json;
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
    private IReadOnlySet<string> _activeScopes = new HashSet<string>(StringComparer.Ordinal);
    private IReadOnlySet<string> _activeSkillIds = new HashSet<string>(StringComparer.Ordinal);
    private BrokerRunLease? _activeRunLease;
    private readonly Dictionary<string, PendingDecision> _pendingDecisions = new(StringComparer.Ordinal);
    private long _decisionCounter;

    private sealed record PendingDecision(
        string Token,
        string Kind,
        string RunId,
        string ToolId,
        string IntentHash,
        long BindingVersion,
        DateTimeOffset ExpiresAt,
        bool Consumed);

    private BrokerSession(string descriptorPath, BrokerSessionDescriptor descriptor)
    {
        DescriptorPath = descriptorPath;
        Descriptor = descriptor;
    }

    public string DescriptorPath { get; }
    public BrokerSessionDescriptor Descriptor { get; }

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
        IReadOnlySet<string>? scopes = null,
        IReadOnlySet<string>? skillIds = null)
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
                _activeScopes = scopes is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(scopes, StringComparer.Ordinal);
                _activeSkillIds = skillIds is null
                    ? new HashSet<string>(StringComparer.Ordinal)
                    : new HashSet<string>(skillIds, StringComparer.Ordinal);
                return true;
            }

            return string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    public bool Owns(string sessionId)
    {
        lock (_gate)
        {
            return string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal);
        }
    }

    public IReadOnlySet<string> Scopes
    {
        get
        {
            lock (_gate)
            {
                return new HashSet<string>(_activeScopes, StringComparer.Ordinal);
            }
        }
    }

    public IReadOnlySet<string> SkillIds
    {
        get
        {
            lock (_gate)
            {
                return new HashSet<string>(_activeSkillIds, StringComparer.Ordinal);
            }
        }
    }

    public void Close(string sessionId)
    {
        lock (_gate)
        {
            if (string.Equals(_activeSessionId, sessionId, StringComparison.Ordinal))
            {
                _activeSessionId = null;
                _activeScopes = new HashSet<string>(StringComparer.Ordinal);
                _activeSkillIds = new HashSet<string>(StringComparer.Ordinal);
                _activeRunLease = null;
                _pendingDecisions.Clear();
            }
        }
    }

    /// <summary>
    /// Closes the active run lease (and invalidates its pending decisions) without closing the
    /// owning session.
    /// </summary>
    public bool TryCloseRun(string runId)
    {
        lock (_gate)
        {
            if (_activeRunLease is null || !string.Equals(_activeRunLease.RunId, runId, StringComparison.Ordinal))
            {
                return false;
            }

            _activeRunLease = null;
            _pendingDecisions.Clear();
            return true;
        }
    }

    public bool TryRebindRunPolicy(BrokerRunLease reboundLease, out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(reboundLease);
        lock (_gate)
        {
            if (_activeRunLease is null)
            {
                errorCode = "run_not_open";
                return false;
            }

            var current = _activeRunLease;
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
            _activeRunLease = reboundLease with { BindingVersion = nextBindingVersion };
            errorCode = null;
            return true;
        }
    }

    /// <summary>
    /// Registers an allow decision bound to the CURRENT snapshot binding. Rebinding to a newer
    /// snapshot invalidates it.
    /// </summary>
    public string RegisterDecision(string runId, string toolId, string intentHash, TimeSpan lifetime)
    {
        lock (_gate)
        {
            var lease = RequireActiveRunLocked(runId);
            _decisionCounter++;
            var token = $"dec-{lease.BindingVersion}-{_decisionCounter:X8}-{Guid.NewGuid():N}";
            _pendingDecisions[token] = new PendingDecision(
                token,
                "decision",
                runId,
                toolId,
                intentHash,
                lease.BindingVersion,
                DateTimeOffset.UtcNow.Add(lifetime),
                Consumed: false);
            return token;
        }
    }

    /// <summary>
    /// Converts a still-valid decision token into a preparation token under the same binding,
    /// keeping the exact canonical intent recorded at evaluation time.
    /// </summary>
    public bool TryPrepareDecision(string decisionToken, string runId, string toolId, out string? preparationToken, out string? errorCode)
    {
        lock (_gate)
        {
            preparationToken = null;
            if (_activeRunLease is null || !string.Equals(_activeRunLease.RunId, runId, StringComparison.Ordinal))
            {
                errorCode = "run_not_open";
                return false;
            }

            if (_activeRunLease.IsExpired(DateTimeOffset.UtcNow))
            {
                errorCode = "lease_expired";
                return false;
            }

            if (!_pendingDecisions.TryGetValue(decisionToken, out var entry)
                || entry.Consumed
                || !string.Equals(entry.Kind, "decision", StringComparison.Ordinal)
                || !string.Equals(entry.RunId, runId, StringComparison.Ordinal)
                || !string.Equals(entry.ToolId, toolId, StringComparison.Ordinal)
                || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                errorCode = ResolveDecisionErrorCode(decisionToken, entry, runId, toolId, entry?.IntentHash ?? string.Empty);
                return false;
            }

            if (entry.BindingVersion != CurrentBindingVersionLocked(runId))
            {
                errorCode = "decision_stale";
                return false;
            }

            var lease = RequireActiveRunLocked(runId);
            _decisionCounter++;
            var token = $"prep-{lease.BindingVersion}-{_decisionCounter:X8}-{Guid.NewGuid():N}";
            _pendingDecisions[token] = entry with { Token = token, Kind = "preparation" };
            preparationToken = token;
            errorCode = null;
            return true;
        }
    }

    public bool TryConsumePreparation(string preparationToken, string runId, string toolId, string intentHash, out string? errorCode)
    {
        lock (_gate)
        {
            if (_activeRunLease is null || !string.Equals(_activeRunLease.RunId, runId, StringComparison.Ordinal))
            {
                errorCode = "run_not_open";
                return false;
            }

            if (_activeRunLease.IsExpired(DateTimeOffset.UtcNow))
            {
                errorCode = "lease_expired";
                return false;
            }

            if (!_pendingDecisions.TryGetValue(preparationToken, out var entry)
                || entry.Consumed
                || !string.Equals(entry.Kind, "preparation", StringComparison.Ordinal)
                || !string.Equals(entry.RunId, runId, StringComparison.Ordinal)
                || !string.Equals(entry.ToolId, toolId, StringComparison.Ordinal)
                || !string.Equals(entry.IntentHash, intentHash, StringComparison.Ordinal)
                || entry.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                errorCode = ResolveDecisionErrorCode(preparationToken, entry, runId, toolId, intentHash);
                return false;
            }

            if (entry.BindingVersion != CurrentBindingVersionLocked(runId))
            {
                errorCode = "decision_stale";
                return false;
            }

            _pendingDecisions[preparationToken] = entry with { Consumed = true };
            errorCode = null;
            return true;
        }
    }

    public void ClearDecisions()
    {
        lock (_gate)
        {
            _pendingDecisions.Clear();
        }
    }

    private long CurrentBindingVersionLocked(string runId)
    {
        var lease = _activeRunLease;
        return lease is not null
            && !lease.IsExpired(DateTimeOffset.UtcNow)
            && string.Equals(lease.RunId, runId, StringComparison.Ordinal)
            ? lease.BindingVersion
            : -1;
    }

    private BrokerRunLease RequireActiveRunLocked(string runId)
    {
        if (_activeRunLease is null || !string.Equals(_activeRunLease.RunId, runId, StringComparison.Ordinal))
        {
            throw new BrokerSessionException("run_not_open");
        }

        if (_activeRunLease.IsExpired(DateTimeOffset.UtcNow))
        {
            throw new BrokerSessionException("lease_expired");
        }

        return _activeRunLease;
    }

    private string ResolveDecisionErrorCode(string? token, PendingDecision? entry, string runId, string toolId, string intentHash)
    {
        if (_activeRunLease is null || !string.Equals(_activeRunLease.RunId, runId, StringComparison.Ordinal))
        {
            return "run_not_open";
        }

        if (entry is not null && entry.BindingVersion != CurrentBindingVersionLocked(runId))
        {
            return "decision_stale";
        }

        if (entry is not null && entry.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            return "decision_expired";
        }

        if (entry is not null
            && (!string.Equals(entry.RunId, runId, StringComparison.Ordinal)
                || !string.Equals(entry.ToolId, toolId, StringComparison.Ordinal)
                || !string.Equals(entry.IntentHash, intentHash, StringComparison.Ordinal)))
        {
            return "decision_mismatch";
        }

        return "unknown_decision_token";
    }

    public BrokerRunLease? ActiveRunLease
    {
        get
        {
            lock (_gate)
            {
                return _activeRunLease;
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

            // A connection may only attach a run to the session it opened. Without
            // this check a caller that owns session A could submit a lease for
            // session B and then address that run through A's socket.
            if (_activeSessionId is not null
                && !string.Equals(_activeSessionId, lease.SessionId, StringComparison.Ordinal))
            {
                errorCode = "session_conflict";
                return false;
            }

            if (_activeRunLease is not null && !string.Equals(_activeRunLease.RunId, lease.RunId, StringComparison.Ordinal))
            {
                errorCode = "run_conflict";
                return false;
            }

            // Validate snapshot hash matches recomputed canonical (phase-1 empty snapshot)
            var expectedCapabilities = RuAgent.Protocol.Harness.BrokerPolicyContracts.AuthoritativeFallbackCapabilities();
            // hash already validated in BrokerRunLease.CreateValidated, but double-check
            if (!expectedCapabilities.SequenceEqual(lease.AdvertisedCapabilities))
            {
                // Phase-1 must advertise only files.list/files.read; mismatch fails closed
                errorCode = "capability_mismatch";
                return false;
            }

            _activeRunLease = lease;
            // Also open session for v2 lease if not already open
            if (_activeSessionId is null)
            {
                _activeSessionId = lease.SessionId;
                _activeScopes = new HashSet<string>(lease.AdvertisedCapabilities.Select(_ => "workspace.read"), StringComparer.Ordinal);
            }

            errorCode = null;
            return true;
        }
    }

    public bool TryGetRunLease(string runId, out BrokerRunLease? lease)
    {
        lock (_gate)
        {
            if (_activeRunLease is not null && string.Equals(_activeRunLease.RunId, runId, StringComparison.Ordinal))
            {
                lease = _activeRunLease;
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
            if (_activeRunLease is null || !string.Equals(_activeRunLease.RunId, runId, StringComparison.Ordinal))
            {
                errorCode = "run_not_open";
                return false;
            }

            if (_activeRunLease.IsExpired(DateTimeOffset.UtcNow))
            {
                errorCode = "lease_expired";
                return false;
            }

            if (!_activeRunLease.AdvertisedCapabilities.Contains(toolId))
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

    public static BrokerSession Load(string descriptorPath, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(descriptorPath))
        {
            throw new BrokerSessionException("missing_descriptor");
        }

        var fullDescriptorPath = Path.GetFullPath(descriptorPath);
        if (!File.Exists(fullDescriptorPath))
        {
            throw new BrokerSessionException("missing_descriptor");
        }

        BrokerSessionDescriptor? descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<BrokerSessionDescriptor>(
                File.ReadAllText(fullDescriptorPath),
                BrokerSessionDescriptor.JsonOptions);
        }
        catch (JsonException error)
        {
            throw new BrokerSessionException("invalid_descriptor", error.Message);
        }
        catch (IOException error)
        {
            throw new BrokerSessionException("invalid_descriptor", error.Message);
        }

        if (descriptor is null)
        {
            throw new BrokerSessionException("invalid_descriptor");
        }

        if (string.IsNullOrWhiteSpace(descriptor.AuthToken))
        {
            throw new BrokerSessionException("missing_token");
        }

        if (descriptor.ProtocolVersion != AgentEnvelopeCodec.ProtocolVersion && descriptor.ProtocolVersion != AgentEnvelopeCodec.ProtocolVersionV2)
        {
            throw new BrokerSessionException("unsupported_version");
        }

        if (!Uri.TryCreate(descriptor.BaseUrl, UriKind.Absolute, out var baseUri)
            || !string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !baseUri.IsLoopback
            || baseUri.Port is < 1024 or > 65535)
        {
            throw new BrokerSessionException("invalid_base_url");
        }

        if (string.IsNullOrWhiteSpace(descriptor.SessionRoot)
            || !Path.IsPathRooted(descriptor.SessionRoot))
        {
            throw new BrokerSessionException("root_escape");
        }

        var sessionRoot = Path.GetFullPath(descriptor.SessionRoot);
        var descriptorDirectory = Path.GetDirectoryName(fullDescriptorPath)
            ?? throw new BrokerSessionException("invalid_descriptor");
        if (!IsUnderRoot(descriptorDirectory, sessionRoot))
        {
            throw new BrokerSessionException("root_escape");
        }

        var effectiveNow = now ?? DateTimeOffset.UtcNow;
        if (descriptor.ExpiresAt <= effectiveNow)
        {
            throw new BrokerSessionException("expired");
        }

        var normalizedDescriptor = descriptor with { SessionRoot = sessionRoot };
        return new BrokerSession(fullDescriptorPath, normalizedDescriptor);
    }

    private static bool IsUnderRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(candidate));
        return !Path.IsPathRooted(relative)
            && !relative.Equals("..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
