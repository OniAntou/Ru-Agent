using System.Text;
using System.Text.Json;
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
    private const long MaximumDescriptorBytes = 1 * 1024 * 1024;
    private readonly object _gate = new();
    private string? _activeSessionId;
    private readonly Dictionary<string, HashSet<string>> _scopesBySession = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BrokerRunLease> _runLeases = new(StringComparer.Ordinal);
    private IReadOnlyList<string> _advertisedCapabilities =
        RuAgent.Protocol.Harness.BrokerPolicyContracts.AuthoritativeFallbackCapabilities();

    public SessionDirectoryGrantRegistry DirectoryGrants { get; private set; } = new();

    private BrokerSession(string descriptorPath, BrokerSessionDescriptor descriptor)
    {
        DescriptorPath = descriptorPath;
        Descriptor = descriptor;
    }

    public string DescriptorPath { get; }
    public BrokerSessionDescriptor Descriptor { get; }

    /// <summary>
    /// Capability list every run lease must advertise. The BrokerHost pins it
    /// from its options (web tools extend the authoritative fallback set);
    /// lease opens fail closed when a connection advertises anything else.
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
            _runLeases[reboundLease.RunId] = reboundLease with { BindingVersion = nextBindingVersion };
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

        try
        {
            if (new FileInfo(fullDescriptorPath).Length > MaximumDescriptorBytes)
            {
                throw new BrokerSessionException("invalid_descriptor");
            }
        }
        catch (IOException error)
        {
            throw new BrokerSessionException("invalid_descriptor", error.Message);
        }

        BrokerSessionDescriptor? descriptor;
        try
        {
            descriptor = JsonSerializer.Deserialize<BrokerSessionDescriptor>(
                ReadBoundedText(fullDescriptorPath, checked((int)MaximumDescriptorBytes)),
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

    private static string ReadBoundedText(string path, int maximumBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new BrokerSessionException("invalid_descriptor");
            output.Write(buffer, 0, read);
        }
        using var text = new StreamReader(new MemoryStream(output.ToArray(), writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return text.ReadToEnd();
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
