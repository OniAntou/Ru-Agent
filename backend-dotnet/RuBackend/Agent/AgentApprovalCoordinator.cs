using System.Text.Json.Nodes;
using RuAgent.Protocol;
using RuBackend.Conversation;
using RuBackend.Worker;

namespace RuBackend.Agent;

public interface IAgentApprovalCoordinator
{
    ChatEvent Register(WorkerToolRequest tool, AgentToolResult result);

    Task<AgentToolResult> RespondAsync(
        AgentApprovalResponse response,
        CancellationToken cancellationToken);
}

public sealed class AgentApprovalCoordinator : IAgentApprovalCoordinator
{
    private const int ApprovalDeadlineMs = 10_000;
    private readonly IAgentBrokerClient? _broker;
    private readonly AgentApprovalStore _store;

    public AgentApprovalCoordinator(
        IAgentBrokerClient? broker = null,
        AgentApprovalStore? store = null)
    {
        _broker = broker;
        _store = store ?? new AgentApprovalStore();
    }

    public ChatEvent Register(WorkerToolRequest tool, AgentToolResult result)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(result);
        if (!string.Equals(result.Status, ToolResultStatuses.ApprovalRequired, StringComparison.Ordinal))
        {
            return new ChatEvent("agent.activity", result.SafeSummary, tool.SessionId);
        }

        try
        {
            var approval = new AgentApprovalRequest(
                ReadString(result.Output, "approval_id", 128),
                tool.SessionId,
                ReadString(result.Output, "tool_id", 128),
                ReadString(result.Output, "tool_version", 64),
                ReadString(result.Output, "safe_action", 256),
                ReadString(result.Output, "scope", 256),
                ReadExpiry(result.Output));
            _store.Put(approval);
            return new ChatEvent(
                "agent.approval_required",
                approval.SafeAction,
                approval.SessionId,
                Status: ToolResultStatuses.ApprovalRequired,
                ApprovalId: approval.ApprovalId,
                ToolId: approval.ToolId,
                Scope: approval.Scope,
                ExpiresAt: approval.ExpiresAt);
        }
        catch (Exception) when (result.Output is not null)
        {
            return new ChatEvent(
                "agent.approval_result",
                "Approval metadata was invalid.",
                tool.SessionId,
                "approval_invalid",
                Status: ToolResultStatuses.Denied);
        }
    }

    public async Task<AgentToolResult> RespondAsync(
        AgentApprovalResponse response,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!_store.TryTake(response.ApprovalId, response.SessionId, out _))
        {
            return Denied(response.ApprovalId, "approval_stale", "Approval is missing, expired, or already used.");
        }

        if (_broker is null)
        {
            return new AgentToolResult(
                response.ApprovalId,
                ToolResultStatuses.Failed,
                new JsonObject(),
                "agent_unavailable",
                "Agent tools are unavailable.");
        }

        try
        {
            return await _broker.RespondToApprovalAsync(
                response,
                $"approval-{response.ApprovalId}",
                ApprovalDeadlineMs,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new AgentToolResult(
                response.ApprovalId,
                ToolResultStatuses.Cancelled,
                new JsonObject(),
                "cancelled",
                "Approval response cancelled.");
        }
        catch (AgentBrokerException error)
        {
            return new AgentToolResult(
                response.ApprovalId,
                ToolResultStatuses.Failed,
                new JsonObject(),
                error.Code,
                "Approval response failed.");
        }
    }

    private static AgentToolResult Denied(string requestId, string code, string summary) =>
        new(requestId, ToolResultStatuses.Denied, new JsonObject(), code, summary);

    private static string ReadString(JsonObject payload, string key, int maxLength) =>
        payload[key] is JsonValue value
        && value.TryGetValue<string>(out var result)
        && !string.IsNullOrWhiteSpace(result)
        && result.Length <= maxLength
            ? result
            : throw new InvalidDataException("approval_metadata_invalid");

    private static DateTimeOffset ReadExpiry(JsonObject payload) =>
        payload["expires_at"] is JsonValue value
        && value.TryGetValue<string>(out var text)
        && DateTimeOffset.TryParse(text, out var expiresAt)
        && expiresAt > DateTimeOffset.UtcNow
            ? expiresAt
            : throw new InvalidDataException("approval_metadata_invalid");
}
