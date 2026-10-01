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

        // Hex digests are case-insensitive; the host computes and stores lowercase,
        // so a client echoing uppercase must not be reported as stale.
        var normalizedSnapshotHash = snapshotHash.ToLowerInvariant();

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

        var reconciliation = await ReadReconciliationContextAsync(runId, cancellationToken)
            .ConfigureAwait(false);
        if (reconciliation.IsMalformed)
        {
            return BoundedError(409, "reconciliation_target_invalid", "Reconciliation target is invalid.");
        }

        var intentCode = reconciliation.IntentCode;
        var freshHash = await _snapshots.ComputeFreshHashAsync(runId, cancellationToken).ConfigureAwait(false);
        if (freshHash is null || !string.Equals(freshHash, normalizedSnapshotHash, StringComparison.Ordinal))
        {
            return new AgentServiceResult(409, new
            {
                error_code = "reconcile_stale",
                message = "Workspace snapshot changed; re-read before deciding.",
                snapshot_hash = freshHash,
                intent_code = intentCode,
            });
        }

        return await ApplyReconciliationAsync(
                run,
                command,
                intentCode,
                reconciliation.Target,
                cancellationToken)
            .ConfigureAwait(false);
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
        ReconciliationResolutionTarget? target,
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
        var evt = new HarnessEventIntent(
            _ids.NewEventId(),
            HarnessEventKind.ReconciliationResolved,
            modelStepId: target?.ModelStepId,
            toolCallId: target?.ToolCallId,
            executionId: target?.ExecutionId,
            safeCode: command.ToWireToken());
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

    private async Task<ReconciliationContext> ReadReconciliationContextAsync(
        RunId runId,
        CancellationToken cancellationToken)
    {
        string? intentCode = null;
        ReconciliationResolutionTarget? target = null;
        var isMalformed = false;
        await foreach (var envelope in _store
            .ReadEventsAsync(runId, 0, cancellationToken)
            .ConfigureAwait(false))
        {
            var item = envelope.Event;
            if (!string.Equals(item.Kind, "reconciliation.required", StringComparison.Ordinal))
            {
                continue;
            }

            intentCode = item.SafeCode ?? intentCode;
            if (item.ModelStepId is null && item.ToolCallId is null && item.ExecutionId is null)
            {
                target = null;
                continue;
            }

            if (item.ModelStepId is not { } modelStepId
                || item.ToolCallId is not { } toolCallId
                || item.ExecutionId is not { } executionId)
            {
                isMalformed = true;
                continue;
            }

            target = new ReconciliationResolutionTarget(modelStepId, toolCallId, executionId);
        }

        return new ReconciliationContext(intentCode, target, isMalformed);
    }

    private sealed record ReconciliationResolutionTarget(
        ModelStepId ModelStepId,
        ToolCallId ToolCallId,
        ExecutionId ExecutionId);

    private sealed record ReconciliationContext(
        string? IntentCode,
        ReconciliationResolutionTarget? Target,
        bool IsMalformed);

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
                            new { name = "/skills", arguments = "", description = "List workspace skills (name + description)." },
                            new { name = "/skill", arguments = "<name>", description = "Load one workspace skill's SKILL.md body." },
                        },
                    },
                    run.Version);

            case AgentCommandKind.Skills:
                return DispatchSkills(run, runIdRaw, cancellationToken);

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
                return DispatchSkill(run, runIdRaw, command, cancellationToken);

            default:
                return BoundedError(400, "command_invalid", "Unsupported Agent command.");
        }
    }

    private AgentServiceResult DispatchSkills(
        AgentRun run,
        string runIdRaw,
        CancellationToken cancellationToken)
    {
        var workspaceRoot = ResolveRunWorkspaceRoot(run.RunId);
        if (workspaceRoot is null)
        {
            return BoundedError(409, "workspace_unknown", "The run has no workspace root.");
        }

        var loader = new RuBackend.Harness.Prompting.MarkdownSkillLoader();
        var roots = new[]
        {
            Path.Combine(workspaceRoot, "skills"),
            Path.Combine(workspaceRoot, ".ru", "skills"),
        };
        var loaded = loader.Load(roots, cancellationToken);
        var items = loaded.Skills
            .OrderBy(skill => skill.Id, StringComparer.Ordinal)
            .Select(skill => new
            {
                name = skill.Id,
                description = skill.Description,
            })
            .ToArray();
        return CommandSucceeded(runIdRaw, "skills", new { items }, run.Version);
    }

    private AgentServiceResult DispatchSkill(
        AgentRun run,
        string runIdRaw,
        AgentCommand command,
        CancellationToken cancellationToken)
    {
        if (command.SkillId is null)
        {
            return BoundedError(400, "command_invalid", "Skill requires a skill name.");
        }

        var workspaceRoot = ResolveRunWorkspaceRoot(run.RunId);
        if (workspaceRoot is null)
        {
            return BoundedError(409, "workspace_unknown", "The run has no workspace root.");
        }

        var loader = new RuBackend.Harness.Prompting.MarkdownSkillLoader();
        var roots = new[]
        {
            Path.Combine(workspaceRoot, "skills"),
            Path.Combine(workspaceRoot, ".ru", "skills"),
        };
        var loaded = loader.Load(roots, cancellationToken);
        var skill = loaded.Skills.FirstOrDefault(item =>
            string.Equals(item.Id, command.SkillId, StringComparison.OrdinalIgnoreCase));
        if (skill is null)
        {
            return BoundedError(404, "skill_not_found", "The requested skill was not found.");
        }

        return CommandSucceeded(
            runIdRaw,
            "skill",
            new
            {
                name = skill.Id,
                description = skill.Description,
                body = skill.Body,
                body_sha256 = skill.ContentSha256,
            },
            run.Version);
    }

    private string? ResolveRunWorkspaceRoot(RunId runId) =>
        _runtimeRoots is not null && _runtimeRoots.TryGetRoot(runId, out var root) ? root : null;

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
