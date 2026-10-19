using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Runtime;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Protocol;

namespace RuBackend.Harness.Broker;

/// <summary>
/// Builds one <see cref="InProcessToolGateway"/> per run lease against a shared
/// <see cref="BrokerExecutionRuntime"/>: validates the snapshot identity, opens
/// the run lease through the runtime's own open path (the only lease
/// validator), and tracks open gateways so run teardown, session-task control
/// and grants revoke can reach them.
/// </summary>
public sealed partial class InProcessToolGatewayFactory : IToolGatewayFactory
{
    /// <summary>
    /// The wire-only identity fields <see cref="BrokerApprovalState"/> validates
    /// leases against. In-process there is no socket, so the identity is a
    /// process-stable inert loopback URL and the digest of the empty bearer.
    /// </summary>
    private static readonly string InertBrokerUrl =
        new Uri("ws://127.0.0.1:0/agent").AbsoluteUri;

    private static readonly string InertAuthTokenSha256 =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Empty))).ToLowerInvariant();

    private readonly Lazy<BrokerExecutionRuntime> _runtime;
    private readonly BrokerApprovalState _approvals = new();
    private readonly ConcurrentDictionary<string, InProcessToolGateway> _gateways =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BrokerRunLeaseIdentity> _leases =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private readonly Lazy<Subagents.SubagentControlDispatcher>? _subagents;
    private readonly object _sync = new();

    public InProcessToolGatewayFactory(BrokerExecutionRuntime runtime, Lazy<Subagents.SubagentControlDispatcher>? subagents = null)
        : this(new Lazy<BrokerExecutionRuntime>(
            CreateComposed(runtime),
            LazyThreadSafetyMode.ExecutionAndPublication), subagents)
    {
    }

    private static Func<BrokerExecutionRuntime> CreateComposed(BrokerExecutionRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return () => runtime;
    }

    /// <summary>
    /// Lazy composition seam for the Host: the factory is created at host
    /// startup (the runtime loop resolves it), but the runtime — and with it
    /// the tool service and its spool cleanup — must not be composed until the
    /// first tool call.
    /// </summary>
    internal InProcessToolGatewayFactory(Lazy<BrokerExecutionRuntime> runtime, Lazy<Subagents.SubagentControlDispatcher>? subagents = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _subagents = subagents;
    }

    private BrokerExecutionRuntime Runtime => _runtime.Value;

    /// <summary>Always true: the injected runtime is the configured authority.</summary>
    public bool IsConfigured => true;

    public Task ReclaimSubagentWorkspaceAsync(RuAgent.Protocol.Subagents.SubagentCollectionSource source, CancellationToken ct) =>
        Runtime.ReclaimSubagentWorkspaceAsync(source, ct);

    public Task ReclaimSubagentAllocationAsync(RuAgent.Protocol.Subagents.SubagentWorkspaceAllocation evidence,
        string stateDirectory, CancellationToken ct) => Runtime.ReclaimSubagentAllocationAsync(evidence, stateDirectory, ct);

    public Task<RuAgent.Broker.Subagents.Workspaces.SubagentWorkspaceManifest> ProvisionSubagentWorkspaceAsync(
        RunId parentRunId, string agentId, string stateDirectory, CancellationToken cancellationToken,
        RuAgent.Protocol.Harness.BrokerPolicySnapshotWire? parentCeiling = null,
        RuAgent.Protocol.Subagents.ISubagentWorkspaceAllocationObserver? observer = null) =>
        Runtime.ProvisionSubagentWorkspaceAsync(parentRunId.Value, agentId, stateDirectory, cancellationToken, parentCeiling, observer);

    public Task<RuAgent.Protocol.Subagents.SubagentChangeSet> CollectSubagentChangesAsync(
        RuAgent.Protocol.Subagents.SubagentCollectionSource source, long resultVersion,
        IReadOnlyDictionary<string, string> appliedBaseHashes, CancellationToken ct) =>
        Runtime.CollectSubagentChangesAsync(source, resultVersion, appliedBaseHashes, ct);

    public Task BindDelegationAsync(RunId parentRunId, RunId childRunId,
        RuAgent.Protocol.Subagents.SubagentRole role, CancellationToken cancellationToken) =>
        Runtime.BindDelegationAsync(parentRunId.Value, childRunId.Value, role, cancellationToken);

    public Task BindDelegationAsync(RunId parentRunId, RunId childRunId,
        RuAgent.Protocol.Subagents.SubagentRole role,
        RuAgent.Protocol.Subagents.SubagentParentPolicyIdentity expectedParentPolicy, CancellationToken cancellationToken,
        RuAgent.Protocol.Harness.BrokerPolicySnapshotWire? parentCeiling = null) =>
        Runtime.BindDelegationAsync(parentRunId.Value, childRunId.Value, role, cancellationToken, expectedParentPolicy, parentCeiling);

    public async Task<IToolGateway> CreateAsync(
        string workspaceRoot,
        PolicySnapshot policySnapshot,
        RunId runId,
        string? agentSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policySnapshot);
        if (policySnapshot.Sha256.Length != 64)
        {
            throw new InvalidOperationException(
                "broker_gateway_requires_canonical_snapshot_hash");
        }

        // Session id derivation is stable per chat: the Broker session is the
        // Host Agent session id, stable across every drive and run of one
        // chat, because session grants and approval revoke key on it. Callers
        // without an Agent session fall back to a run-stable id.
        var sessionId = !string.IsNullOrWhiteSpace(agentSessionId)
            ? agentSessionId.Trim()
            : "session_" + runId.Value;
        if (sessionId.Length > 128 || sessionId.Contains('\0'))
        {
            throw new InvalidOperationException("broker_gateway_requires_bounded_session_id");
        }

        var canonicalRoot = Path.GetFullPath(workspaceRoot);
        var requestedLease = new BrokerRunLeaseIdentity(
            runId.Value,
            sessionId,
            policySnapshot.Workspace.WorkspaceId.Value,
            canonicalRoot,
            policySnapshot.Version,
            policySnapshot.Sha256,
            string.Empty,
            BrokerUrl: InertBrokerUrl,
            AuthTokenSha256: InertAuthTokenSha256);

        // Same-scope replacement create/rebind cycles serialize exactly like
        // the old lock did; only the open await leaves the lock now, so a
        // cancelled open never parks a thread-pool thread and teardown cannot
        // interleave between the runtime open and the lease install.
        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = FindReusableGateway(runId.Value, requestedLease);
            if (existing is not null)
            {
                return existing;
            }

            BrokerRunLeaseIdentity lease;
            lock (_sync)
            {
                var generation = _leases.TryGetValue(runId.Value, out var previousLease)
                    ? previousLease!.Generation + 1
                    : 1;
                lease = requestedLease with
                {
                    LeaseId = Guid.NewGuid().ToString("N"),
                    Generation = generation,
                };
            }

            await OpenLeaseThroughRuntimeAsync(lease, policySnapshot, canonicalRoot, cancellationToken)
                .ConfigureAwait(false);

            bool raced;
            InProcessToolGateway gateway;
            lock (_sync)
            {
                var racingGateway = FindReusableGateway(runId.Value, requestedLease);
                if (racingGateway is not null)
                {
                    // A racing creator of the same scope installed first;
                    // prefer its gateway over ours.
                    raced = true;
                    gateway = racingGateway;
                }
                else
                {
                    raced = false;
                    _approvals.ActivateLease(lease);
                    _leases[runId.Value] = lease;

                    if (_gateways.TryGetValue(runId.Value, out var existingGateway)
                        && !existingGateway.IsDisposed)
                    {
                        existingGateway.Rebind(lease);
                        gateway = existingGateway;
                    }
                    else
                    {
                        _gateways.TryRemove(runId.Value, out _);
                        var created = new InProcessToolGateway(Runtime, lease, _approvals, _subagents);
                        _gateways[runId.Value] = created;
                        gateway = created;
                    }
                }
            }

            if (raced)
            {
                // Our freshly opened lease is surplus: close it so the
                // authority does not accumulate open leases for one run. The
                // close stays outside the lock because it awaits.
                await CloseLeaseThroughRuntimeAsync(lease).ConfigureAwait(false);
            }

            return gateway;
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Returns the run's live gateway when its installed lease still matches
    /// the requested policy scope, so a same-scope Create reuses it (and its
    /// approval state) instead of rotating the lease.
    /// </summary>
    private InProcessToolGateway? FindReusableGateway(
        string runIdValue,
        BrokerRunLeaseIdentity requestedLease)
    {
        lock (_sync)
        {
            if (_leases.TryGetValue(runIdValue, out var existingLease)
                && BrokerGatewayContracts.SameLeaseScope(existingLease!, requestedLease)
                && _gateways.TryGetValue(runIdValue, out var gateway)
                && !gateway.IsDisposed)
            {
                return gateway;
            }
        }

        return null;
    }

    /// <summary>
    /// Opens (or replaces) the run lease through the runtime's open path. The
    /// runtime validates the shared run-open payload, capability advertisement,
    /// session binding and expiry; a refused open fails Create closed instead
    /// of returning a gateway that cannot execute.
    /// </summary>
    private async Task OpenLeaseThroughRuntimeAsync(
        BrokerRunLeaseIdentity lease,
        PolicySnapshot policySnapshot,
        string canonicalRoot,
        CancellationToken cancellationToken)
    {
        var request = new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersionV2,
            "open-" + lease.RunId,
            AgentMessageTypes.RunOpen,
            lease.SessionId,
            null,
            10_000,
            BrokerGatewayContracts.BuildRunOpenPayload(
                lease.SessionId,
                lease.RunId,
                canonicalRoot,
                policySnapshot));
        var opened = await Runtime.OpenRunAsync(request, cancellationToken).ConfigureAwait(false);
        if (opened.Error is not null)
        {
            throw new InvalidOperationException(opened.Error.ErrorCode);
        }
    }

    /// <summary>
    /// Lists the run session's bounded background tasks and optionally stops one.
    /// Returns a failed payload when the run has no live gateway.
    /// </summary>
    public async Task<JsonObject> ListSessionTasksAsync(
        string runId,
        string? killExecutionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        if (!_gateways.TryGetValue(runId, out var gateway))
        {
            return new JsonObject
            {
                ["status"] = "failed",
                ["error_code"] = "broker_gateway_unavailable",
            };
        }

        return await gateway.ListSessionTasksAsync(killExecutionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Bridges an approval resolution from the Host approval endpoint to the
    /// runtime pending approval that produced it. Returns false when the run
    /// has no tracked gateway or the authority rejects the resolution; the
    /// authorization token, when issued, stays cached run-scoped until the
    /// resumed proposal consumes it.
    /// </summary>
    public async Task<(bool Authorized, string? AuthorizationToken)> RespondApprovalAsync(
        string runId,
        string hostApprovalId,
        string command,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostApprovalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (!_gateways.TryGetValue(runId, out var gateway))
        {
            return (false, null);
        }

        var lease = gateway.Lease;
        if (!_approvals.TryGetAsk(
            runId,
            lease,
            hostApprovalId,
            out var brokerApprovalId,
            out var argsKey))
        {
            return (false, null);
        }

        return await gateway
            .RespondApprovalAsync(brokerApprovalId, command, argsKey, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes one Agent session's directory grants before durable session
    /// deletion. True when revoked or when no gateway is live (grants cannot
    /// execute without one); false only on explicit authority rejection, which
    /// must fail the delete closed.
    /// </summary>
    public async Task<bool> RevokeSessionGrantsAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        InProcessToolGateway[] gateways;
        lock (_sync)
        {
            gateways = _gateways.Values.ToArray();
        }

        if (gateways.Length == 0)
        {
            return true;
        }

        foreach (var gateway in gateways)
        {
            try
            {
                if (await gateway.RevokeSessionGrantsAsync(sessionId, cancellationToken).ConfigureAwait(false))
                {
                    return true;
                }

                return false;
            }
            catch (ToolTransportException)
            {
                continue;
            }
        }

        return true;
    }

    /// <summary>
    /// Closes one Agent session on the authority and drops its Host-side state.
    /// The runtime close removes the session's run leases, drops its pending
    /// approvals and invokes the execution service's session teardown (terminal tasks,
    /// approvals, todos, spools); the factory then disposes every
    /// gateway tracked for that session. A session the authority never opened
    /// is a no-op: the lazy runtime is not composed for a delete that has no
    /// authority state to release.
    /// </summary>
    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (!_runtime.IsValueCreated)
        {
            return;
        }

        await _createGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InProcessToolGateway[] sessionGateways;
            lock (_sync)
            {
                sessionGateways = _gateways.Values
                    .Where(gateway => string.Equals(gateway.Lease.SessionId, sessionId, StringComparison.Ordinal))
                    .ToArray();
                foreach (var gateway in sessionGateways)
                {
                    _approvals.ClearRun(gateway.Lease.RunId);
                    _leases.TryRemove(gateway.Lease.RunId, out _);
                    _gateways.TryRemove(gateway.Lease.RunId, out _);
                }
            }

            foreach (var gateway in sessionGateways)
            {
                await DisposeGatewayAsync(gateway).ConfigureAwait(false);
            }

            await Runtime.CloseSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Drops the run's Host-side state, closes its lease on the authority and
    /// disposes its gateway. The wire session died with its connection; the
    /// in-process session lives for the host, so a run torn down here must
    /// release its authority lease instead of accumulating until host
    /// shutdown. Closing is best effort: teardown must not fail on a run the
    /// authority has already closed.
    /// </summary>
    public async Task CloseRunAsync(string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        await _createGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            InProcessToolGateway? gateway;
            BrokerRunLeaseIdentity? lease;
            lock (_sync)
            {
                _approvals.ClearRun(runId);
                _leases.TryRemove(runId, out lease);
                _gateways.TryRemove(runId, out gateway);
            }

            if (lease is not null)
            {
                await CloseLeaseThroughRuntimeAsync(lease).ConfigureAwait(false);
            }

            if (gateway is not null)
            {
                await DisposeGatewayAsync(gateway).ConfigureAwait(false);
            }
        }
        finally
        {
            _createGate.Release();
        }
    }

    /// <summary>
    /// Closes the authority-side run lease through the runtime's own close
    /// path, which also cancels anything still running for the run.
    /// </summary>
    private async Task CloseLeaseThroughRuntimeAsync(BrokerRunLeaseIdentity lease)
    {
        var request = new AgentEnvelope(
            AgentEnvelopeCodec.ProtocolVersionV2,
            "close-" + lease.RunId,
            AgentMessageTypes.RunClose,
            lease.SessionId,
            null,
            10_000,
            new JsonObject { ["run_id"] = lease.RunId });
        try
        {
            await Runtime.CloseRunAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best effort: the session close releases any remaining lease, and
            // the runtime reports an already-closed run as a typed result
            // rather than throwing, so an exception here is a teardown anomaly
            // that must not break run cleanup.
        }
    }

    private static async Task DisposeGatewayAsync(InProcessToolGateway gateway)
    {
        try
        {
            await gateway.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _createGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            KeyValuePair<string, InProcessToolGateway>[] gateways;
            lock (_sync)
            {
                gateways = _gateways.ToArray();
                _gateways.Clear();
                _leases.Clear();
                _approvals.Clear();
            }

            foreach (var pair in gateways)
            {
                await pair.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _createGate.Release();
        }
    }
}
