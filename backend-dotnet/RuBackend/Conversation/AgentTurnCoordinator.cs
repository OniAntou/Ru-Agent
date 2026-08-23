using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuBackend.Agent;
using RuBackend.Diagnostics;
using RuBackend.Worker;

namespace RuBackend.Conversation;

public sealed record AgentTurnRequest(
    WorkerToolRequest ToolRequest,
    Func<ChatEvent, ValueTask>? PublishAsync,
    CapabilityProfile Profile);

public interface IAgentTurnCoordinator
{
    Task<IReadOnlyList<AgentToolCapability>> GetModelCapabilitiesAsync(
        CapabilityProfile profile,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ChatEvent> HandleToolTurnAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken);

    Task<WorkerToolResult> ExecuteToolAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken);
}

public sealed class AgentTurnCoordinator : IAgentTurnCoordinator
{
    private static readonly IReadOnlyList<AgentToolCapability> NoCapabilities =
        Array.AsReadOnly(Array.Empty<AgentToolCapability>());

    private readonly IAgentBrokerClient? _broker;
    private readonly IAgentApprovalCoordinator? _approvals;
    private readonly IDiagnosticEmitter? _diagnostics;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private AgentSessionGrant? _activeSession;

    public AgentTurnCoordinator(
        IAgentBrokerClient? broker = null,
        IAgentApprovalCoordinator? approvals = null,
        IDiagnosticEmitter? diagnostics = null)
    {
        _broker = broker;
        _approvals = approvals;
        _diagnostics = diagnostics;
    }

    public Task<IReadOnlyList<AgentToolCapability>> GetModelCapabilitiesAsync(
        CapabilityProfile profile,
        CancellationToken cancellationToken) => _broker is null
        ? Task.FromResult(NoCapabilities)
        : _broker.GetCapabilitiesAsync(profile, cancellationToken);

    public async IAsyncEnumerable<ChatEvent> HandleToolTurnAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var events = new List<ChatEvent>();
        var originalPublisher = request.PublishAsync;
        var collectingRequest = request with
        {
            PublishAsync = async value =>
            {
                events.Add(value);
                if (originalPublisher is not null)
                {
                    await originalPublisher(value).ConfigureAwait(false);
                }
            },
        };
        _ = await ExecuteToolAsync(collectingRequest, cancellationToken).ConfigureAwait(false);
        foreach (var item in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }

    public static (CapabilityProfile Profile, bool Clamped) ProfileForTurn(
        string? mode,
        bool legacyAgentMode)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            return mode switch
            {
                "chat" => (CapabilityProfile.Chat, false),
                "agent" => (CapabilityProfile.Agent, false),
                "full" => (CapabilityProfile.Full, false),
                _ => (CapabilityProfile.Chat, true),
            };
        }

        return legacyAgentMode
            ? (CapabilityProfile.Agent, false)
            : (CapabilityProfile.Chat, false);
    }

    public async Task<WorkerToolResult> ExecuteToolAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tool = request.ToolRequest;
        var correlationId = ResolveCorrelationId(tool.CorrelationId);
        if (correlationId is null)
        {
            return await FailAsync(
                request,
                "invalid_correlation",
                "Tool request correlation is invalid.",
                cancellationToken).ConfigureAwait(false);
        }

        EmitAction(
            DiagnosticCodes.ToolRequested,
            tool,
            correlationId,
            status: "started");
        await PublishAsync(request, Activity(tool, "requested"), cancellationToken)
            .ConfigureAwait(false);

        if (_broker is null)
        {
            return await FailAsync(
                request,
                "agent_unavailable",
                "Agent tools are unavailable.",
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var capabilities = await GetModelCapabilitiesAsync(
                request.Profile,
                cancellationToken).ConfigureAwait(false);
            var capability = capabilities.FirstOrDefault(item =>
                item.ModelEnabled
                && string.Equals(item.ToolId, tool.ToolId, StringComparison.Ordinal)
                && string.Equals(item.ToolVersion, tool.ToolVersion, StringComparison.Ordinal));
            if (capability is null)
            {
                return await FailAsync(
                    request,
                    "capability_unavailable",
                    "Tool capability is unavailable.",
                    cancellationToken).ConfigureAwait(false);
            }

            var capabilityClass = CapabilityClass(capability);
            if (!string.Equals(tool.CapabilityClass, capabilityClass, StringComparison.Ordinal))
            {
                return await FailAsync(
                    request,
                    "capability_mismatch",
                    "Tool capability does not match the request.",
                    cancellationToken).ConfigureAwait(false);
            }

            var (grant, opened) = await EnsureSessionAsync(
                tool,
                capability,
                correlationId,
                request.Profile,
                cancellationToken).ConfigureAwait(false);
            if (opened)
            {
                await PublishAsync(
                    request,
                    new ChatEvent(
                        "agent.session",
                        SessionId: grant.SessionId,
                        Scopes: grant.Scopes.Order(StringComparer.Ordinal).ToArray()),
                    cancellationToken).ConfigureAwait(false);
            }

            if (!capability.Scopes.All(grant.Scopes.Contains))
            {
                return await FailAsync(
                    request,
                    "scope_not_granted",
                    "Tool scope is not granted.",
                    cancellationToken).ConfigureAwait(false);
            }

            var result = await _broker.ExecuteToolAsync(
                new AgentToolCall(
                    tool.RequestId,
                    tool.SessionId,
                    correlationId,
                    tool.ToolId,
                    tool.ToolVersion,
                    capabilityClass,
                    tool.Arguments.ToDictionary(
                        pair => pair.Key,
                        pair => (object?)pair.Value?.DeepClone(),
                        StringComparer.Ordinal),
                    tool.DeadlineMs),
                request.Profile,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (!string.Equals(result.RequestId, tool.RequestId, StringComparison.Ordinal))
            {
                return await FailAsync(
                    request,
                    "receipt_mismatch",
                    "Tool receipt did not match the request.",
                    cancellationToken).ConfigureAwait(false);
            }

            if (!IsConsistentReceipt(result))
            {
                return await FailAsync(
                    request,
                    "invalid_tool_receipt",
                    "Tool receipt was invalid.",
                    cancellationToken).ConfigureAwait(false);
            }

            EmitReceiptTrail(tool, correlationId, result);

            if (string.Equals(result.Status, ToolResultStatuses.ApprovalRequired, StringComparison.Ordinal))
            {
                var approval = _approvals?.Register(tool, result)
                    ?? new ChatEvent(
                        "agent.approval_required",
                        "Agent is waiting for approval.",
                        tool.SessionId,
                        Status: ToolResultStatuses.ApprovalRequired);
                await PublishAsync(request, approval, cancellationToken).ConfigureAwait(false);
            }

            var workerResult = MapResult(result);
            if (!string.Equals(workerResult.Status, ToolResultStatuses.Cancelled, StringComparison.Ordinal))
            {
                await PublishAsync(
                    request,
                    Activity(tool, workerResult.Status, workerResult.ErrorCode),
                    cancellationToken).ConfigureAwait(false);
            }

            return workerResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseSessionAsync().ConfigureAwait(false);
            return Cancelled(request.ToolRequest.RequestId);
        }
        catch (AgentBrokerException error)
        {
            return await FailAsync(
                request,
                error.Code,
                "Agent tool failed.",
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<(AgentSessionGrant Grant, bool Opened)> EnsureSessionAsync(
        WorkerToolRequest tool,
        AgentToolCapability capability,
        string correlationId,
        CapabilityProfile profile,
        CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existing = _activeSession;
            if (existing is not null
                && string.Equals(existing.SessionId, tool.SessionId, StringComparison.Ordinal)
                && existing.ExpiresAt > DateTimeOffset.UtcNow
                && capability.Scopes.All(existing.Scopes.Contains))
            {
                return (existing, false);
            }

            if (existing is not null)
            {
                await _broker!.CloseSessionAsync(
                    existing.SessionId,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
                _activeSession = null;
            }

            var grant = await _broker!.OpenSessionAsync(
                tool.SessionId,
                await TurnScopesAsync(profile, cancellationToken).ConfigureAwait(false),
                new HashSet<string>(StringComparer.Ordinal),
                correlationId,
                tool.DeadlineMs,
                cancellationToken).ConfigureAwait(false);
            if (!capability.Scopes.All(grant.Scopes.Contains))
            {
                // Retain ownership until defensive close succeeds so cleanup
                // can be retried without forgetting the Broker's active owner.
                _activeSession = grant;
                await _broker.CloseSessionAsync(
                    grant.SessionId,
                    correlationId,
                    cancellationToken).ConfigureAwait(false);
                _activeSession = null;
                throw new AgentBrokerException("scope_not_granted");
            }

            _activeSession = grant;
            return (grant, true);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private async Task<IReadOnlySet<string>> TurnScopesAsync(
        CapabilityProfile profile,
        CancellationToken cancellationToken)
    {
        var capabilities = await GetModelCapabilitiesAsync(profile, cancellationToken)
            .ConfigureAwait(false);
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in capabilities)
        {
            scopes.UnionWith(capability.Scopes);
        }

        return scopes;
    }

    private async Task ReleaseSessionAsync()
    {
        if (_broker is null)
        {
            return;
        }

        AgentSessionGrant? grant;
        await _sessionLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            grant = _activeSession;
            _activeSession = null;
        }
        finally
        {
            _sessionLock.Release();
        }

        if (grant is null)
        {
            return;
        }

        try
        {
            // Release must never mask the original cancellation, so the close
            // roundtrip runs detached from the cancelled turn token.
            await _broker.CloseSessionAsync(
                grant.SessionId,
                null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (AgentBrokerException)
        {
        }
    }

    private async Task<WorkerToolResult> FailAsync(
        AgentTurnRequest request,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken)
    {
        var result = Failed(request.ToolRequest.RequestId, errorCode, safeSummary);
        await PublishAsync(
            request,
            Activity(request.ToolRequest, result.Status, result.ErrorCode),
            cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static WorkerToolResult MapResult(AgentToolResult result) => new(
        result.RequestId,
        result.Status,
        result.Output.DeepClone().AsObject(),
        result.ErrorCode,
        BoundedSummary(result.SafeSummary),
        result.DurationMs,
        result.VerificationState,
        result.EndpointRole,
        result.EndpointHash);

    private static WorkerToolResult Failed(string requestId, string errorCode, string safeSummary) =>
        new(
            requestId,
            ToolResultStatuses.Failed,
            new JsonObject(),
            errorCode,
            BoundedSummary(safeSummary),
            0,
            ToolVerificationStates.Failed,
            null,
            null);

    private static WorkerToolResult Cancelled(string requestId) => new(
        requestId,
        ToolResultStatuses.Cancelled,
        new JsonObject(),
        "cancelled",
        "Tool cancelled.",
        0,
        ToolVerificationStates.Failed,
        null,
        null);

    private static string CapabilityClass(AgentToolCapability capability) =>
        string.Equals(capability.ToolId, "system.volume", StringComparison.Ordinal)
        && string.Equals(capability.ToolVersion, "1.0.0", StringComparison.Ordinal)
            ? "native_mutation"
            : "internal";

    private static string? ResolveCorrelationId(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return CorrelationContext.GenerateId();
        }

        return CorrelationContext.IsValid(candidate) ? candidate : null;
    }

    private static bool IsConsistentReceipt(AgentToolResult result)
    {
        if (!result.AdapterInvoked
            && string.Equals(result.Status, ToolResultStatuses.Succeeded, StringComparison.Ordinal)
            && string.Equals(result.VerificationState, ToolVerificationStates.Verified, StringComparison.Ordinal))
        {
            return false;
        }

        if (result.AdapterInvoked
            && result.Status is ToolResultStatuses.Denied or ToolResultStatuses.ApprovalRequired)
        {
            return false;
        }

        if (string.Equals(result.VerificationState, ToolVerificationStates.Verified, StringComparison.Ordinal)
            && !string.Equals(result.Status, ToolResultStatuses.Succeeded, StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(result.VerificationState, ToolVerificationStates.Pending, StringComparison.Ordinal)
            && (!string.Equals(result.Status, ToolResultStatuses.ApprovalRequired, StringComparison.Ordinal)
                || result.AdapterInvoked))
        {
            return false;
        }

        return !string.Equals(result.Status, ToolResultStatuses.ApprovalRequired, StringComparison.Ordinal)
            || string.Equals(result.VerificationState, ToolVerificationStates.Pending, StringComparison.Ordinal);
    }

    private void EmitReceiptTrail(
        WorkerToolRequest tool,
        string correlationId,
        AgentToolResult result)
    {
        if (!result.AdapterInvoked)
        {
            if (string.Equals(result.Status, ToolResultStatuses.Denied, StringComparison.Ordinal))
            {
                EmitAction(
                    DiagnosticCodes.ToolPolicyDenied,
                    tool,
                    correlationId,
                    status: result.Status,
                    errorCode: result.ErrorCode,
                    verificationState: result.VerificationState);
            }
            else if (string.Equals(
                result.Status,
                ToolResultStatuses.ApprovalRequired,
                StringComparison.Ordinal))
            {
                EmitAction(
                    DiagnosticCodes.ToolApprovalRequired,
                    tool,
                    correlationId,
                    status: result.Status,
                    errorCode: result.ErrorCode,
                    verificationState: result.VerificationState);
            }
            return;
        }

        EmitAction(
            DiagnosticCodes.ToolAdapterStarted,
            tool,
            correlationId,
            status: "started");
        EmitAction(
            DiagnosticCodes.ToolAdapterCompleted,
            tool,
            correlationId,
            durationMs: result.DurationMs,
            status: result.Status,
            errorCode: result.ErrorCode,
            verificationState: result.VerificationState,
            endpointRole: result.EndpointRole,
            endpointHash: result.EndpointHash);

        if (string.Equals(result.VerificationState, ToolVerificationStates.Verified, StringComparison.Ordinal))
        {
            EmitAction(
                DiagnosticCodes.ToolVerificationSucceeded,
                tool,
                correlationId,
                durationMs: result.DurationMs,
                status: ToolResultStatuses.Succeeded,
                verificationState: result.VerificationState,
                endpointRole: result.EndpointRole,
                endpointHash: result.EndpointHash);
        }
        else if (string.Equals(result.VerificationState, ToolVerificationStates.Failed, StringComparison.Ordinal))
        {
            EmitAction(
                DiagnosticCodes.ToolVerificationFailed,
                tool,
                correlationId,
                durationMs: result.DurationMs,
                status: ToolResultStatuses.Failed,
                errorCode: result.ErrorCode,
                verificationState: result.VerificationState,
                endpointRole: result.EndpointRole,
                endpointHash: result.EndpointHash);
        }
    }

    private void EmitAction(
        string eventCode,
        WorkerToolRequest tool,
        string correlationId,
        double? durationMs = null,
        string? status = null,
        string? errorCode = null,
        string? verificationState = null,
        string? endpointRole = null,
        string? endpointHash = null)
    {
        try
        {
            _diagnostics?.TryEmit(
                "info",
                "host.agent",
                eventCode,
                correlationId,
                durationMs,
                status,
                errorCode,
                toolId: tool.ToolId,
                toolVersion: tool.ToolVersion,
                verificationState: verificationState,
                endpointRole: endpointRole,
                endpointHash: endpointHash);
        }
        catch
        {
            // Diagnostics must never change the action result.
        }
    }

    private static ChatEvent Activity(
        WorkerToolRequest tool,
        string status,
        string? errorCode = null) => new(
            "agent.activity",
            SessionId: tool.SessionId,
            ErrorCode: errorCode,
            Status: status,
            ToolId: tool.ToolId);

    private static string BoundedSummary(string summary) =>
        string.IsNullOrWhiteSpace(summary)
            ? "Agent tool completed."
            : summary.Length <= 4096 ? summary : summary[..4096];

    private static async ValueTask PublishAsync(
        AgentTurnRequest request,
        ChatEvent value,
        CancellationToken cancellationToken)
    {
        if (request.PublishAsync is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await request.PublishAsync(value).ConfigureAwait(false);
        }
    }
}
