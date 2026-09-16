using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Approval;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Core.Reconciliation;
using RuAgent.Harness.Core.Workspace;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Policy;
using RuAgent.Harness.Persistence.Privacy;
using RuAgent.Harness.Persistence.Workspace;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuBackend.Agent;
using RuBackend.Business.Sessions;
using RuBackend.Business.Settings;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;
using RuBackend.Providers;
using RuAgent.Runtime.Adapters.Models;

namespace RuBackend.Harness;

public sealed partial class AgentV1Service
{
    /// <summary>
    /// Applies the typed user reconciliation decision. The snapshot hash the client
    /// presents must equal this host's freshly computed workspace/Git hash; anything
    /// else fails closed with reconcile_stale and leaves the run untouched.
    /// </summary>
    public async Task<AgentServiceResult> ReconcileAsync(
        string runIdRaw,
        string? outcome,
        string? snapshotHash,
        CancellationToken cancellationToken)
    {
        if (!IsRunId(runIdRaw))
        {
            return BoundedError(400, "invalid_run_id", "run_id invalid.");
        }

        if (!ReconciliationCommand.TryParse(outcome, out var command))
        {
            return BoundedError(400, "invalid_outcome", "outcome must be applied, not_applied, or unknown_continue.");
        }

        if (string.IsNullOrWhiteSpace(snapshotHash)
            || snapshotHash.Length != 64
            || !snapshotHash.All(char.IsAsciiHexDigit))
        {
            return BoundedError(400, "invalid_snapshot_hash", "snapshot_hash must be a sha-256 hex digest.");
        }

        if (_snapshots is null || _inspectionGate is null)
        {
            return BoundedError(409, "reconciliation_unavailable", "Reconciliation services are not available.");
        }

        var runId = new RunId(runIdRaw);
        var run = await _store.GetRunAsync(runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            return BoundedError(404, "run_not_found", "Run not found.");
        }

        if (run.State != HarnessRunState.ReconciliationRequired)
        {
            return BoundedError(409, "invalid_run_state", "Run is not awaiting reconciliation.");
        }

        string? intentCode = null;
        await foreach (var envelope in _store.ReadEventsAsync(runId, 0, cancellationToken).ConfigureAwait(false))
        {
            if (envelope.Event.Kind == "reconciliation.required"
                && envelope.Event.SafeCode is { } code)
            {
                intentCode = code;
            }
        }

        var freshHash = await _snapshots.ComputeFreshHashAsync(runId, cancellationToken).ConfigureAwait(false);
        if (freshHash is null || !string.Equals(freshHash, snapshotHash, StringComparison.Ordinal))
        {
            return new AgentServiceResult(409, new
            {
                error_code = "reconcile_stale",
                message = "Workspace snapshot changed; re-read before deciding.",
                snapshot_hash = freshHash,
                intent_code = intentCode,
            });
        }

        return await ApplyReconciliationAsync(run, command, intentCode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits one accepted reconciliation decision. Shared by the strict
    /// reconcile endpoint (which first proves the caller saw the current
    /// workspace hash) and by Continue on a parked run, where the user's
    /// explicit continue is itself the decision and no snapshot proof is
    /// required.
    /// </summary>
    private async Task<AgentServiceResult> ApplyReconciliationAsync(
        AgentRun run,
        ReconciliationCommand command,
        string? intentCode,
        CancellationToken cancellationToken)
    {
        var runId = run.RunId;
        var runIdRaw = runId.Value;
        if (_inspectionGate is null)
        {
            return BoundedError(409, "reconciliation_unavailable", "Reconciliation services are not available.");
        }

        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.ApplyReconciliation);
        if (!transition.Accepted)
        {
            return BoundedError(409, transition.ErrorCode ?? "invalid_transition", "Reconcile rejected.");
        }

        if (command.Kind == ReconciliationCommandKind.UnknownContinue)
        {
            _inspectionGate.Activate(runId);
        }

        var now = _time.UtcNow;
        var nextRun = AdvanceRun(run, transition.Next, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow, 1, now);
        var evt = new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.ReconciliationResolved, safeCode: command.ToWireToken());
        var commit = new HarnessCommit(runId, run.Version, nextRun, new[] { evt }, Array.Empty<HarnessPayloadIntent>(), Array.Empty<ConversationOutboxIntent>());
        var result = await _store.CommitAsync(commit, cancellationToken).ConfigureAwait(false);
        if (result.Outcome != HarnessCommitOutcome.Committed)
        {
            return BoundedError(409, "commit_conflict", "Commit conflict.");
        }

        return new AgentServiceResult(200, new
        {
            run_id = runIdRaw,
            state = MapRunState(result.Run!.Status),
            resolution = command.ToWireToken(),
            intent_code = intentCode,
        });
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
                            new { name = "/skills", arguments = "", description = "List skills granted to this session." },
                            new { name = "/skill", arguments = "<id> <json-object>", description = "Run one granted declarative skill." },
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

            default:
                return BoundedError(400, "command_invalid", "Unsupported Agent command.");
        }
    }

    private async Task<AgentServiceResult> DispatchSkillsAsync(
        AgentRun run,
        string runIdRaw,
        CancellationToken cancellationToken)
    {
        if (_agentBrokerClient is null)
        {
            return BoundedError(409, "broker_not_ready", "The Agent Broker is not ready.");
        }

        IReadOnlyList<AgentSkillDescriptor> skills;
        try
        {
            skills = await _agentBrokerClient
                .ListSkillsAsync(run.SessionId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentBrokerException exception)
        {
            return BrokerCommandError(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return BoundedError(502, "broker_unavailable", "The Agent Broker could not be reached.");
        }

        var items = skills
            .OrderBy(skill => skill.SkillId, StringComparer.Ordinal)
            .ThenBy(skill => skill.Version, StringComparer.Ordinal)
            .Select(skill => new
            {
                skill_id = skill.SkillId,
                version = skill.Version,
                required_scopes = skill.RequiredScopes.Order(StringComparer.Ordinal).ToArray(),
                required_tools = skill.RequiredTools.Order(StringComparer.Ordinal).ToArray(),
                trust_class = skill.TrustClass,
            })
            .ToArray();
        return CommandSucceeded(runIdRaw, "skills", new { items }, run.Version);
    }

    private async Task<AgentServiceResult> DispatchSkillAsync(
        AgentRun run,
        string runIdRaw,
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        if (_agentBrokerClient is null)
        {
            return BoundedError(409, "broker_not_ready", "The Agent Broker is not ready.");
        }

        IReadOnlyList<AgentSkillDescriptor> skills;
        try
        {
            skills = await _agentBrokerClient
                .ListSkillsAsync(run.SessionId.Value, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentBrokerException exception)
        {
            return BrokerCommandError(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return BoundedError(502, "broker_unavailable", "The Agent Broker could not be reached.");
        }

        var descriptor = skills
            .Where(skill => string.Equals(skill.SkillId, command.SkillId, StringComparison.Ordinal))
            .OrderByDescending(skill => ParseSkillVersion(skill.Version))
            .ThenByDescending(skill => skill.Version, StringComparer.Ordinal)
            .FirstOrDefault();
        if (descriptor is null || command.JsonInput is null || command.SkillId is null)
        {
            return BoundedError(403, "skill_not_granted", "The requested skill is not granted.");
        }

        AgentSkillExecutionResult result;
        try
        {
            result = await _agentBrokerClient
                .ExecuteSkillAsync(
                    run.SessionId.Value,
                    command.SkillId,
                    descriptor.Version,
                    command.JsonInput,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AgentBrokerException exception)
        {
            return BrokerCommandError(exception.Code);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return BoundedError(502, "broker_unavailable", "The Agent Broker could not be reached.");
        }

        var data = new
        {
            skill_id = result.SkillId,
            version = result.Version,
            safe_summary = result.SafeSummary,
            receipt_id = result.ReceiptId,
            step_count = result.StepCount,
            output = result.Output,
        };
        var response = new DispatchAgentCommandResponse(
            runIdRaw,
            result.Status,
            result.ErrorCode ?? "skill",
            data,
            run.Version);

        return result.Status switch
        {
            ToolResultStatuses.Succeeded => new AgentServiceResult(200, response),
            ToolResultStatuses.ApprovalRequired => new AgentServiceResult(409, response),
            ToolResultStatuses.Denied => new AgentServiceResult(403, response),
            ToolResultStatuses.Cancelled => new AgentServiceResult(409, response),
            ToolResultStatuses.TimedOut => new AgentServiceResult(504, response),
            _ => new AgentServiceResult(502, response),
        };
    }

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
