using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using static RuBackend.Harness.AgentV1Shared;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness;

public sealed partial class AgentCommandsService
{
    private readonly IHarnessEventStore _store;
    private readonly IIdGenerator _ids;
    private readonly ITimeProvider _time;
    private readonly IAgentRuntimeRootRegistry _runtimeRoots;
    private readonly Broker.IToolGatewayFactory _brokerGateways;
    private readonly AgentV1SubagentGuard _subagentGuard;

    public Func<bool> PythonAvailable { get; set; } = static () => PythonAvailability.IsAvailable;

    public AgentCommandsService(IHarnessEventStore store, IIdGenerator ids, ITimeProvider time,
        IAgentRuntimeRootRegistry runtimeRoots, Broker.IToolGatewayFactory brokerGateways,
        AgentV1SubagentGuard subagentGuard)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _ids = ids ?? throw new ArgumentNullException(nameof(ids));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _runtimeRoots = runtimeRoots ?? throw new ArgumentNullException(nameof(runtimeRoots));
        _brokerGateways = brokerGateways ?? throw new ArgumentNullException(nameof(brokerGateways));
        _subagentGuard = subagentGuard ?? throw new ArgumentNullException(nameof(subagentGuard));
    }


    public async Task<AgentServiceResult> CompactAsync(
        string runIdRaw,
        CompactAgentRunRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (await _subagentGuard.IsInternalRunAsync(new RunId(runIdRaw), cancellationToken).ConfigureAwait(false))
            return BoundedError(409, "subagent_read_only", "Subagents are controlled by the main agent. Send your request to the parent.");

        var strategyRaw = request?.Strategy?.Trim().ToLowerInvariant();
        var strategy = strategyRaw switch
        {
            null or "" or "normal" => ContextCompactionStrategy.Normal,
            "aggressive" => ContextCompactionStrategy.Aggressive,
            _ => (ContextCompactionStrategy?)null,
        };
        if (strategy is null)
        {
            return BoundedError(
                400,
                "invalid_compaction_strategy",
                "strategy must be normal or aggressive.");
        }

        var runId = new RunId(runIdRaw);
        if (!_runtimeRoots.TryGetCoordinator(runId, out var coordinator)
            || coordinator is null)
        {
            return BoundedError(
                409,
                "runtime_not_ready",
                "The run runtime is not ready for compaction.");
        }

        ContextCompactionResult result;
        try
        {
            result = await coordinator.CompactAsync(
                    runId,
                    strategy.Value,
                    ContextCompactionReason.Manual,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }
        catch (InvalidOperationException exception) when (exception.Message == "run_not_found")
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        return result.Outcome switch
        {
            ContextCompactionOutcome.Committed => new AgentServiceResult(
                200,
                new CompactAgentRunResponse(
                    runIdRaw,
                    MapRunState(result.Run!.Status),
                    strategyRaw is "aggressive" ? "aggressive" : "normal",
                    result.CompactionId,
                    result.BeforeTokens,
                    result.AfterTokens,
                    result.Run.Version)),
            ContextCompactionOutcome.NoOp => new AgentServiceResult(
                200,
                new CompactAgentRunResponse(
                    runIdRaw,
                    "no_op",
                    strategyRaw is "aggressive" ? "aggressive" : "normal",
                    null,
                    null,
                    null,
                    result.Run?.Version ?? 0)),
            ContextCompactionOutcome.NotAvailable => BoundedError(
                409,
                result.ErrorCode ?? "compact_not_available",
                "Context compaction is not available."),
            ContextCompactionOutcome.CommitConflict => BoundedError(
                409,
                "commit_conflict",
                "Commit conflict."),
            ContextCompactionOutcome.RunNotFound => BoundedError(
                404,
                "run_not_found",
                "Run not found."),
            _ => BoundedError(500, "compact_failed", "Compaction failed."),
        };
    }

    public async Task<AgentServiceResult> DispatchCommandAsync(
        string runIdRaw,
        DispatchAgentCommandRequest? request,
        CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (await _subagentGuard.IsInternalRunAsync(new RunId(runIdRaw), cancellationToken).ConfigureAwait(false))
            return BoundedError(409, "subagent_read_only", "Subagents are controlled by the main agent. Send your request to the parent.");

        if (request is null || request.Text is null)
        {
            return BoundedError(400, "invalid_request", "Command text is required.");
        }

        var parsed = AgentActionRouter.Parse(request.Text);
        if (!parsed.IsCommand)
        {
            return BoundedError(400, "command_invalid", "A typed Agent command is required.");
        }

        if (!parsed.Success || parsed.Command is null)
        {
            return BoundedError(
                400,
                parsed.Code ?? "command_invalid",
                parsed.SafeMessage);
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        var command = parsed.Command;
        if (run.State.IsTerminal()
            && command.Kind is AgentCommandKind.Compact or AgentCommandKind.Skill)
        {
            return BoundedError(409, "run_terminal", "Terminal runs cannot execute Agent commands.");
        }

        switch (command.Kind)
        {
            case AgentCommandKind.Help:
                return CommandSucceeded(
                    runIdRaw,
                    "help",
                    new
                    {
                        commands = new[]
                        {
                            new { name = "/compact", arguments = "normal|aggressive", description = "Compact the run context." },
                            new { name = "/help", arguments = "", description = "List typed Agent commands." },
                            new { name = "/skills", arguments = "", description = "List workspace skills (name + description)." },
                            new { name = "/skill", arguments = "<id> [arguments]", description = "Activate a skill workflow in this run." },
                            new { name = "/tasks", arguments = "kill <execution_id> | read <execution_id> [offset] [limit]", description = "List this session's background tasks, stop one, or read one task's output." },
                        },
                    },
                    run.Version);

            case AgentCommandKind.Skills:
                return await DispatchSkillsAsync(run, runIdRaw, cancellationToken).ConfigureAwait(false);

            case AgentCommandKind.Compact:
            {
                var compact = await CompactAsync(
                    runIdRaw,
                    new CompactAgentRunRequest(command.Strategy),
                    cancellationToken).ConfigureAwait(false);
                if (compact.StatusCode != 200)
                {
                    return compact;
                }

                var runVersion = compact.Body is CompactAgentRunResponse response
                    ? response.RunVersion
                    : run.Version;
                return CommandSucceeded(runIdRaw, "compact", compact.Body, runVersion);
            }

            case AgentCommandKind.Skill:
                return await DispatchSkillAsync(run, runIdRaw, command, cancellationToken).ConfigureAwait(false);

            case AgentCommandKind.Tasks:
                return await DispatchTasksAsync(runIdRaw, run, command, cancellationToken).ConfigureAwait(false);

            default:
                return BoundedError(400, "command_invalid", "Unsupported Agent command.");
        }
    }

    private async Task<AgentServiceResult> DispatchTasksAsync(
        string runIdRaw,
        AgentRun run,
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        if (_brokerGateways is not { IsConfigured: true })
        {
            return BoundedError(503, "broker_gateway_unavailable", "The Broker is not available.");
        }

        // One bounded verb per command: stop one task, or page one task's
        // retained output. The Broker owns both semantics.
        var request = new RuAgent.Protocol.SessionTaskRequest(
            KillExecutionId: command.TaskKillId,
            ReadExecutionId: command.TaskReadId,
            Offset: command.TaskReadOffset,
            Limit: command.TaskReadLimit);
        JsonObject payload;
        try
        {
            payload = await _brokerGateways
                .ListSessionTasksAsync(runIdRaw, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ToolTransportException error)
        {
            return BoundedError(503, error.Message, "The Broker connection failed.");
        }

        if (!string.Equals(payload["status"]?.GetValue<string>(), ToolResultStatuses.Succeeded, StringComparison.Ordinal))
        {
            var code = payload["error_code"]?.GetValue<string>() ?? "session_tasks_failed";
            return BoundedError(409, code, "Session task control failed.");
        }

        return CommandSucceeded(
            runIdRaw,
            command.TaskReadId is null ? "tasks" : "task_output",
            payload,
            run.Version);
    }

    private string? ResolveRunWorkspaceRoot(RunId runId) =>
        _runtimeRoots.TryGetRoot(runId, out var root) ? root : null;

    private static AgentServiceResult CommandSucceeded(
        string runId,
        string code,
        object? data,
        long runVersion) => new(
        200,
        new DispatchAgentCommandResponse(
            runId,
            ToolResultStatuses.Succeeded,
            code,
            data,
            runVersion));

    private static AgentServiceResult BrokerCommandError(string code) => code switch
    {
        "skill_not_granted" or "scope_denied" or "session_expired" =>
            BoundedError(403, code, "The Agent Broker denied the skill request."),
        "approval_required" => BoundedError(409, code, "The skill requires user approval."),
        "broker_not_ready" or "broker_unavailable" =>
            BoundedError(409, "broker_not_ready", "The Agent Broker is not ready."),
        _ => BoundedError(502, "broker_unavailable", "The Agent Broker rejected the request."),
    };

    private static Version ParseSkillVersion(string value) =>
        Version.TryParse(value, out var version) ? version : new Version(0, 0, 0);
}
