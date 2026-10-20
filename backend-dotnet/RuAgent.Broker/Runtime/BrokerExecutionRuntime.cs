using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Runtime;

/// <summary>
/// Transport-neutral execution authority for one host process: it owns the
/// session, the execution service surface, the v2 pending-approval store, the
/// approval-token service, the lifecycle gate that serializes preflight and
/// consumption, and the run-cancellation registry. Callers (the in-process
/// gateway) hand it typed requests; it returns typed results.
/// </summary>
public sealed partial class BrokerExecutionRuntime : IDisposable
{
    private readonly BrokerSession _session;
    private readonly IReadOnlySet<string> _executableScopes;
    private readonly Func<AgentEnvelope, CancellationToken, Task<ToolResult>>? _authenticatedToolHandler;
    private readonly Func<string, string?, JsonObject>? _sessionTasksHandler;
    private readonly Func<string, Task>? _sessionClosedHandler;
    private readonly Func<string, Task>? _sessionGrantsRevokedHandler;
    private readonly PendingApprovalStore _pendingV2;
    private readonly ApprovalTokenService _authTokens;
    private readonly IReadOnlyList<string>? _advertisedCapabilities;
    private readonly SemaphoreSlim _lifecycleGate;
    private readonly RunToolCancellationRegistry _runCancellations;
    private int _disposed;

    /// <summary>
    /// Production wiring: the runtime uses the execution service's own surface
    /// (v1 tool requests and approval responses, authenticated v2 execution,
    /// session-task control and session teardown) so no policy or execution
    /// logic lives outside this type.
    /// </summary>
    public BrokerExecutionRuntime(
        BrokerSession session,
        ToolExecutionService tools,
        PendingApprovalStore? pendingV2Store = null,
        ApprovalTokenService? approvalTokenService = null,
        IReadOnlyList<string>? advertisedCapabilities = null,
        SemaphoreSlim? lifecycleGate = null,
        RunToolCancellationRegistry? runCancellations = null)
        : this(
            session,
            ToolExecutionBackend.From(tools),
            pendingV2Store,
            approvalTokenService,
            advertisedCapabilities,
            lifecycleGate,
            runCancellations)
    {
    }

    /// <summary>
    /// Test seam: the same behavior over explicit execution delegates. Kept
    /// internal so production always speaks to a <see cref="ToolExecutionService"/>.
    /// </summary>
    internal BrokerExecutionRuntime(
        BrokerSession session,
        IReadOnlySet<string> executableScopes,
        Func<AgentEnvelope, CancellationToken, Task<ToolResult>>? authenticatedToolHandler = null,
        Func<string, string?, JsonObject>? sessionTasksHandler = null,
        Func<string, Task>? sessionClosedHandler = null,
        PendingApprovalStore? pendingV2Store = null,
        ApprovalTokenService? approvalTokenService = null,
        IReadOnlyList<string>? advertisedCapabilities = null,
        SemaphoreSlim? lifecycleGate = null,
        RunToolCancellationRegistry? runCancellations = null)
        : this(
            session,
            new ToolExecutionBackend(
                executableScopes,
                authenticatedToolHandler,
                sessionTasksHandler,
                sessionClosedHandler),
            pendingV2Store,
            approvalTokenService,
            advertisedCapabilities,
            lifecycleGate,
            runCancellations)
    {
    }

    private BrokerExecutionRuntime(
        BrokerSession session,
        ToolExecutionBackend backend,
        PendingApprovalStore? pendingV2Store,
        ApprovalTokenService? approvalTokenService,
        IReadOnlyList<string>? advertisedCapabilities,
        SemaphoreSlim? lifecycleGate,
        RunToolCancellationRegistry? runCancellations)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _executableScopes = backend.ExecutableScopes;
        _authenticatedToolHandler = backend.AuthenticatedToolHandler;
        _sessionTasksHandler = backend.SessionTasksHandler;
        _sessionClosedHandler = backend.SessionClosedHandler;
        _sessionGrantsRevokedHandler = backend.SessionGrantsRevokedHandler;
        _pendingV2 = pendingV2Store ?? new PendingApprovalStore();
        _authTokens = approvalTokenService ?? new ApprovalTokenService();
        _advertisedCapabilities = advertisedCapabilities;
        _lifecycleGate = lifecycleGate ?? new SemaphoreSlim(1, 1);
        _runCancellations = runCancellations ?? new RunToolCancellationRegistry();
    }

    /// <summary>The session whose leases, scopes and grants the runtime serves.</summary>
    public BrokerSession Session => _session;

    public Task<SessionOpenResult> OpenSessionAsync(
        string sessionId,
        IReadOnlySet<string> scopes,
        CancellationToken cancellationToken) =>
        UnderLifecycleAsync(() => OpenSessionCore(sessionId, scopes), cancellationToken);

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Pending v2 approvals are keyed by run, so the session's run ids
            // are read before the close removes them: a closed session must not
            // leave consumable approvals behind.
            var runIds = _session.RunIdsForSession(sessionId);
            foreach (var runId in runIds) RevokeDelegatedExecutions(runId);
            _session.Close(sessionId);
            ReleaseSessionDelegations(sessionId);
            foreach (var runId in runIds)
            {
                _runCancellations.CancelRun(runId);
                _pendingV2.RemoveRun(runId);
            }

            // Teardown is idempotent and also retries cleanup after a previous
            // close removed authority but failed while draining its processes.
            if (_sessionClosedHandler is { } close)
            {
                await close(sessionId).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<RunLeaseResult> OpenRunAsync(AgentEnvelope request, CancellationToken cancellationToken) =>
        UnderLifecycleAsync(() => OpenRunCore(request), cancellationToken);

    public Task<RunLeaseResult> RebindRunPolicyAsync(AgentEnvelope request, CancellationToken cancellationToken) =>
        UnderLifecycleAsync(() => RebindRunPolicyCore(request), cancellationToken);

    public Task<RunCloseResult> CloseRunAsync(AgentEnvelope request, CancellationToken cancellationToken) =>
        UnderLifecycleAsync(() => CloseRunCore(request), cancellationToken);

    /// <summary>
    /// Session-scoped task control. The kill id is validated here (length and
    /// NUL fail closed) before it reaches the execution service.
    /// </summary>
    public Task<SessionTasksResult> SessionTasksAsync(
        string sessionId,
        string? killExecutionId,
        CancellationToken cancellationToken) =>
        UnderLifecycleAsync(() => SessionTasksCore(sessionId, killExecutionId), cancellationToken);

    public Task<SessionGrantsRevokeResult> RevokeSessionGrantsAsync(
        string ownerSessionId,
        string? targetSessionId,
        CancellationToken cancellationToken) =>
        UnderLifecycleTaskAsync(() => RevokeSessionGrantsCoreAsync(ownerSessionId, targetSessionId), cancellationToken);

    /// <summary>
    /// Cancels every execution registered for the run. Deliberately outside the
    /// lifecycle gate: a cancel must reach in-flight work even while another
    /// operation holds the gate.
    /// </summary>
    public int CancelRun(string runId)
    {
        RevokeDelegatedExecutions(runId);
        return _runCancellations.CancelRun(runId);
    }

    /// <summary>
    /// Clears pending approvals and issued tokens. Called on host shutdown;
    /// the runtime does not own the execution service's lifetime.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _pendingV2.Clear();
        _authTokens.Clear();
        foreach (var scope in _delegations.Values)
        {
            scope.ParentLifetime?.Revoke();
            scope.ChildLifetime?.Revoke();
        }
        _delegations.Clear();
        _runCancellations.CancelAll();
    }

    private async Task<T> UnderLifecycleAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return operation();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task<T> UnderLifecycleTaskAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private SessionOpenResult OpenSessionCore(string sessionId, IReadOnlySet<string> scopes)
    {
        if (scopes.Any(scope => !_executableScopes.Contains(scope)))
        {
            return SessionOpenResult.Unavailable();
        }

        if (!_session.Open(sessionId, scopes))
        {
            return SessionOpenResult.Conflict();
        }

        return SessionOpenResult.Success(_session.ScopesFor(sessionId)
            .OrderBy(scope => scope, StringComparer.Ordinal)
            .ToArray());
    }

    private RunLeaseResult OpenRunCore(AgentEnvelope request)
    {
        try
        {
            var lease = ReadRunLease(request);
            if (!string.Equals(request.SessionId, lease.SessionId, StringComparison.Ordinal))
            {
                return new RunLeaseResult(new RuntimeError("session_conflict", "run lease session does not match connection session"));
            }

            if (!_session.TryOpenRunLease(lease, out var errorCode))
            {
                return new RunLeaseResult(new RuntimeError(errorCode ?? "lease_failed", "run lease failed"));
            }

            return new RunLeaseResult(
                null,
                lease.RunId,
                lease.Snapshot.Version,
                lease.BindingVersion,
                lease.AdvertisedCapabilities);
        }
        catch (AgentProtocolException error)
        {
            return new RunLeaseResult(new RuntimeError(error.Code, "invalid run open"));
        }
    }

    private RunLeaseResult RebindRunPolicyCore(AgentEnvelope request)
    {
        try
        {
            var lease = ReadRunLease(request);
            if (!string.Equals(request.SessionId, lease.SessionId, StringComparison.Ordinal))
            {
                return new RunLeaseResult(new RuntimeError("session_conflict", "run lease session does not match connection session"));
            }

            if (!_session.TryRebindRunPolicy(lease, out var errorCode))
            {
                return new RunLeaseResult(new RuntimeError(errorCode ?? "rebind_failed", "run policy rebind failed"));
            }

            if (!_session.TryGetRunLease(lease.RunId, out var bound) || bound is null)
            {
                return new RunLeaseResult(new RuntimeError("run_not_open", "run lease is not open"));
            }

            RefreshDelegatedExecutions(bound);
            return new RunLeaseResult(null, bound.RunId, bound.Snapshot.Version, bound.BindingVersion);
        }
        catch (AgentProtocolException error)
        {
            return new RunLeaseResult(new RuntimeError(error.Code, "invalid rebind"));
        }
    }

    private RunCloseResult CloseRunCore(AgentEnvelope request)
    {
        if (!_session.Owns(request.SessionId))
        {
            return new RunCloseResult(
                new RuntimeError("broker_not_ready", "session is not open"),
                Closed: false,
                RunId: string.Empty);
        }

        var runId = PayloadReads.OptionalString(request.Payload, "run_id") ?? request.RequestId;
        if (!_session.TryGetRunLease(runId, out var lease)
            || !string.Equals(lease!.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            return new RunCloseResult(null, Closed: false, RunId: runId,
                ErrorCode: "run_not_open", SafeSummary: "no matching open run");
        }
        // A closed lease owns no more live work: terminate anything still
        // running for it rather than leaving the process tree orphaned.
        CancelRun(runId);
        if (_session.TryCloseRun(runId))
        {
            return new RunCloseResult(null, Closed: true, RunId: runId);
        }

        return new RunCloseResult(
            null,
            Closed: false,
            RunId: runId,
            ErrorCode: "run_not_open",
            SafeSummary: "no matching open run");
    }

    private async Task<SessionGrantsRevokeResult> RevokeSessionGrantsCoreAsync(string ownerSessionId, string? targetSessionId)
    {
        if (!_session.Owns(ownerSessionId))
        {
            return new SessionGrantsRevokeResult(
                new RuntimeError("broker_not_ready", "session is not open"),
                Revoked: false,
                ErrorCode: null,
                SafeSummary: string.Empty);
        }

        if (string.IsNullOrWhiteSpace(targetSessionId) || targetSessionId.Length > 128)
        {
            return new SessionGrantsRevokeResult(
                null,
                Revoked: false,
                ErrorCode: "invalid_session_id",
                SafeSummary: "invalid agent session id");
        }

        _session.DirectoryGrants.RevokeSession(targetSessionId);
        foreach (var runId in _session.RunIdsForSession(targetSessionId))
        {
            _runCancellations.CancelRun(runId);
        }
        if (_sessionGrantsRevokedHandler is { } revoke)
        {
            await revoke(targetSessionId).ConfigureAwait(false);
        }
        return new SessionGrantsRevokeResult(null, Revoked: true, ErrorCode: null, SafeSummary: "session grants revoked");
    }

    private SessionTasksResult SessionTasksCore(string sessionId, string? killExecutionId)
    {
        if (!_session.Owns(sessionId))
        {
            return SessionTasksResult.Refused(new RuntimeError("broker_not_ready", "session is not open"));
        }

        if (_sessionTasksHandler is null)
        {
            return SessionTasksResult.Refused(new RuntimeError("phase_not_available", "session tasks are not available"));
        }

        if (killExecutionId is { } kill
            && (kill.Length > RuAgent.Protocol.TerminalLimits.MaxExecutionIdChars || kill.Contains('\0')))
        {
            return SessionTasksResult.Failure("invalid_arguments", "kill_execution_id is invalid.");
        }

        try
        {
            return SessionTasksResult.Success(_sessionTasksHandler(sessionId, killExecutionId));
        }
        catch (ArgumentException error)
        {
            return SessionTasksResult.Failure(error.Message);
        }
    }

    private BrokerRunLease ReadRunLease(AgentEnvelope request)
    {
        var payload = request.Payload;
        var runId = ReadRequiredString(payload, "run_id");
        var sessionId = ReadRequiredString(payload, "session_id");
        var workspaceId = ReadRequiredString(payload, "workspace_id");
        var canonicalRoot = ReadRequiredString(payload, "canonical_root");
        var volumeSerial = ReadRequiredString(payload, "volume_serial");
        var rootFileId = ReadRequiredString(payload, "root_file_id");
        var displayHash = ReadRequiredString(payload, "display_hash");
        var preset = ReadRequiredString(payload, "preset");
        var snapshotVersion = ReadRequiredLong(payload, "snapshot_version");
        var snapshotHash = ReadRequiredString(payload, "snapshot_hash");
        var expiresAtStr = ReadRequiredString(payload, "expires_at");
        if (!DateTimeOffset.TryParse(
                expiresAtStr,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var expiresAt))
        {
            throw new AgentProtocolException("invalid_expires_at");
        }

        if (payload["snapshot"] is not JsonObject snapshotNode)
        {
            throw new AgentProtocolException("invalid_run_open");
        }

        var snapshot = BrokerPolicyContracts.ReadSnapshot(snapshotNode);
        return BrokerRunLease.CreateValidated(
            runId,
            sessionId,
            workspaceId,
            canonicalRoot,
            volumeSerial,
            rootFileId,
            displayHash,
            preset,
            snapshotVersion,
            snapshotHash,
            expiresAt,
            snapshot,
            advertisedCapabilities: _advertisedCapabilities);
    }

    private bool TryGetOwnedRunLease(
        AgentEnvelope request,
        string runId,
        out BrokerRunLease? lease,
        out string errorCode)
    {
        if (!_session.Owns(request.SessionId)
            || !_session.TryGetRunLease(runId, out lease)
            || lease is null
            || !string.Equals(lease.SessionId, request.SessionId, StringComparison.Ordinal))
        {
            lease = null;
            errorCode = "run_not_open";
            return false;
        }

        if (lease.IsExpired(DateTimeOffset.UtcNow))
        {
            lease = null;
            errorCode = "lease_expired";
            return false;
        }

        errorCode = string.Empty;
        return true;
    }

    /// <summary>
    /// The run a cancel applies to. A connection owns one lease per run, so the
    /// session id is only the fallback for envelopes that omit run_id.
    /// </summary>
    private static string ReadRunIdOrSession(AgentEnvelope request) =>
        PayloadReads.OptionalString(request.Payload, "run_id") ?? request.SessionId;

    private static string ReadRequiredString(JsonObject payload, string key)
    {
        if (PayloadReads.OptionalString(payload, key) is { } result)
        {
            return result;
        }

        throw new AgentProtocolException($"invalid_{key}");
    }

    private static long ReadRequiredLong(JsonObject payload, string key)
    {
        if (payload[key] is JsonValue value && value.TryGetValue<long>(out var longValue))
        {
            return longValue;
        }

        if (payload[key] is JsonValue intValue && intValue.TryGetValue<int>(out var intResult))
        {
            return intResult;
        }

        throw new AgentProtocolException($"invalid_{key}");
    }

    /// <summary>
    /// The production backend over a <see cref="ToolExecutionService"/>. Tool
    /// execution always uses the authenticated run-bound path; session lifecycle
    /// delegates to the service.
    /// </summary>
    private sealed record ToolExecutionBackend(
        IReadOnlySet<string> ExecutableScopes,
        Func<AgentEnvelope, CancellationToken, Task<ToolResult>>? AuthenticatedToolHandler,
        Func<string, string?, JsonObject>? SessionTasksHandler,
        Func<string, Task>? SessionClosedHandler,
        Func<string, Task>? SessionGrantsRevokedHandler = null)
    {
        public static ToolExecutionBackend From(ToolExecutionService tools)
        {
            ArgumentNullException.ThrowIfNull(tools);
            return new ToolExecutionBackend(
                tools.ListCapabilities().SelectMany(capability => capability.Scopes).ToHashSet(StringComparer.Ordinal),
                (request, cancellationToken) => tools.ExecuteAuthenticatedV2Async(request, cancellationToken),
                tools.HandleSessionTasks,
                tools.RemoveSessionAsync,
                tools.RevokeSessionGrantsAsync);
        }
    }
}
