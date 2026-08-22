using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuBackend.Agent;
using RuBackend.Worker;

namespace RuBackend.Conversation;

public sealed record AgentTurnRequest(
    WorkerToolRequest ToolRequest,
    Func<ChatEvent, ValueTask>? PublishAsync = null);

public interface IAgentTurnCoordinator
{
    Task<IReadOnlyList<AgentToolCapability>> GetModelCapabilitiesAsync(
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
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly Dictionary<string, AgentSessionGrant> _openedSessions =
        new(StringComparer.Ordinal);

    public AgentTurnCoordinator(
        IAgentBrokerClient? broker = null,
        IAgentApprovalCoordinator? approvals = null)
    {
        _broker = broker;
        _approvals = approvals;
    }

    public Task<IReadOnlyList<AgentToolCapability>> GetModelCapabilitiesAsync(
        CancellationToken cancellationToken) => _broker is null
        ? Task.FromResult(NoCapabilities)
        : _broker.GetCapabilitiesAsync(cancellationToken);

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

    public async Task<WorkerToolResult> ExecuteToolAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tool = request.ToolRequest;
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
            var capabilities = await GetModelCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
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
                    tool.CorrelationId,
                    tool.ToolId,
                    tool.ToolVersion,
                    capabilityClass,
                    tool.Arguments.ToDictionary(
                        pair => pair.Key,
                        pair => (object?)pair.Value?.DeepClone(),
                        StringComparer.Ordinal),
                    tool.DeadlineMs),
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
        CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_openedSessions.TryGetValue(tool.SessionId, out var existing)
                && existing.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return (existing, false);
            }

            _openedSessions.Remove(tool.SessionId);
            var grant = await _broker!.OpenSessionAsync(
                tool.SessionId,
                capability.Scopes.ToHashSet(StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                tool.CorrelationId,
                tool.DeadlineMs,
                cancellationToken).ConfigureAwait(false);
            _openedSessions[tool.SessionId] = grant;
            return (grant, true);
        }
        finally
        {
            _sessionLock.Release();
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
