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
    IAsyncEnumerable<ChatEvent> HandleToolTurnAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken);

    Task<WorkerToolResult> ExecuteToolAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken);
}

public sealed class AgentTurnCoordinator : IAgentTurnCoordinator
{
    private readonly IAgentBrokerClient? _broker;
    private readonly IAgentApprovalCoordinator? _approvals;
    private readonly SemaphoreSlim _sessionLock = new(1, 1);
    private readonly HashSet<string> _openedSessions = new(StringComparer.Ordinal);

    public AgentTurnCoordinator(
        IAgentBrokerClient? broker = null,
        IAgentApprovalCoordinator? approvals = null)
    {
        _broker = broker;
        _approvals = approvals;
    }

    public async IAsyncEnumerable<ChatEvent> HandleToolTurnAsync(
        AgentTurnRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        yield return Activity(request.ToolRequest, request.ToolRequest.ToolId);
        var result = await ExecuteToolAsync(request, cancellationToken).ConfigureAwait(false);
        yield return Activity(request.ToolRequest, result.Status);
    }

    public async Task<WorkerToolResult> ExecuteToolAsync(
        AgentTurnRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tool = request.ToolRequest;
        if (_broker is null)
        {
            return new WorkerToolResult(
                tool.RequestId,
                "failed",
                new JsonObject(),
                "agent_unavailable",
                "Agent tools are unavailable.");
        }

        await PublishAsync(
            request,
            Activity(tool, "running"),
            cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureSessionAsync(tool, cancellationToken).ConfigureAwait(false);
            var result = await _broker.ExecuteToolAsync(
                tool.SessionId,
                tool.ToolId,
                tool.ToolVersion,
                tool.Arguments,
                RequiredScope(tool.ToolId),
                tool.CorrelationId,
                tool.DeadlineMs,
                cancellationToken).ConfigureAwait(false);
            var statusEvent = result.Status == ToolResultStatuses.ApprovalRequired
                ? _approvals?.Register(tool, result)
                    ?? new ChatEvent(
                        "agent.approval_required",
                        "Agent is waiting for approval.",
                        tool.SessionId,
                        Status: ToolResultStatuses.ApprovalRequired)
                : Activity(tool, result.Status);
            await PublishAsync(request, statusEvent, cancellationToken).ConfigureAwait(false);
            return new WorkerToolResult(
                tool.RequestId,
                result.Status,
                result.Output.DeepClone().AsObject(),
                result.ErrorCode,
                BoundedSummary(result.SafeSummary));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new WorkerToolResult(
                tool.RequestId,
                "cancelled",
                new JsonObject(),
                "cancelled",
                "Tool cancelled.");
        }
        catch (AgentBrokerException error)
        {
            return new WorkerToolResult(
                tool.RequestId,
                "failed",
                new JsonObject(),
                error.Code,
                "Agent tool failed.");
        }
    }

    private async Task EnsureSessionAsync(
        WorkerToolRequest tool,
        CancellationToken cancellationToken)
    {
        await _sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_openedSessions.Contains(tool.SessionId))
            {
                return;
            }

            await _broker!.OpenSessionAsync(
                tool.SessionId,
                    new HashSet<string>(
                    [
                        "system.read",
                        "system.write",
                        "workspace.read",
                        "terminal.execute",
                        "workspace.write",
                        "web.search",
                        "web.fetch",
                        "calendar.write",
                        "tasks.write",
                        "mail.send",
                        "skill.execute",
                    ],
                    StringComparer.Ordinal),
                new HashSet<string>(StringComparer.Ordinal),
                tool.CorrelationId,
                tool.DeadlineMs,
                cancellationToken).ConfigureAwait(false);
            _openedSessions.Add(tool.SessionId);
        }
        finally
        {
            _sessionLock.Release();
        }
    }

    private static string RequiredScope(string toolId) => toolId switch
    {
        "system.time" or "system.status" or "app.list" => "system.read",
        "system.volume" => "system.write",
        "files.list" or "files.read" or "workspace.search" => "workspace.read",
        "files.write" or "notes.create" => "workspace.write",
        "terminal.run" or "terminal.shell" => "terminal.execute",
        "web.search" => "web.search",
        "web.fetch" => "web.fetch",
        "calendar.create" => "calendar.write",
        "tasks.create" or "tasks.complete" => "tasks.write",
        "mail.send" => "mail.send",
        _ => "agent.tool.unknown",
    };

    private static ChatEvent Activity(WorkerToolRequest tool, string state) =>
        new(
            "agent.activity",
            $"Agent {state}.",
            tool.SessionId);

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
