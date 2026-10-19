using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Context;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Reconciliation;

namespace RuBackend.Harness.Subagents;

/// <summary>Startup-only recovery using the child's immutable delegation, never current defaults.</summary>
public sealed class SubagentRecoveryCoordinator(IHarnessEventStore events, SqliteSubagentStore agents,
    SqliteSubagentScopeStore scopes, SqliteAgentRunProviderSelectionStore pins, IAgentRuntimeRootRegistry roots,
    IToolGatewayFactory gateways, ITimeProvider time, HarnessSqliteOptions options, HarnessPayloadStore payloads,
    SubagentContinuationFactory? continuations = null)
{
    public async Task<bool> IsInternalRunAsync(RunId runId, CancellationToken ct)
    {
        return await agents.IsInternalRunAsync(runId, ct).ConfigureAwait(false);
    }

    public async Task<bool> RecoverAsync(RunId runId, CancellationToken ct)
    {
        var run = await events.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.State.IsTerminal()) return true;
        var owner = await agents.FindOwnerAsync(run.SessionId, ct).ConfigureAwait(false);
        if (owner is null)
        {
            if (!await agents.IsInternalRunAsync(runId, ct).ConfigureAwait(false)) return false;
            await EndAsync(runId, false, "subagent_owner_missing", ct).ConfigureAwait(false);
            return true;
        }
        // A prior successful recovery pass or admission in this Host already
        // owns this run. Never classify its live dispatch as a crashed one.
        if (roots.TryGetRoot(runId, out _)) return true;
        var attempt = await agents.GetLatestAttemptAsync(owner.RootRunId, owner.AgentId, ct).ConfigureAwait(false);
        var parent = await events.GetRunAsync(owner.RootRunId, ct).ConfigureAwait(false);
        if (owner.Canceled || parent is null || parent.State.IsTerminal())
        {
            if (parent is not null) await agents.CancelFamilyAsync(parent.RunId, ct).ConfigureAwait(false);
            await EndAsync(runId, true, "subagent_parent_canceled", ct).ConfigureAwait(false);
            return true;
        }
        if (attempt?.Run.RunId != runId)
        {
            await EndAsync(runId, false, "subagent_attempt_stale", ct).ConfigureAwait(false);
            return true;
        }
        var expired = attempt.AdmittedAt is null ? time.UtcNow - attempt.CreatedAt >= SubagentLimits.AttemptTimeout
            : attempt.DeadlineAt is null || time.UtcNow >= attempt.DeadlineAt;
        if (expired)
        {
            await EndAsync(runId, false, attempt.AdmittedAt is null ? "subagent_admission_timeout" : "subagent_attempt_timeout", ct).ConfigureAwait(false);
            return true;
        }
        try
        {
            if ((await pins.GetAsync(runId, ct).ConfigureAwait(false))?.Selection is null)
                throw new HarnessStoreException("subagent_model_pin_missing");
            if (run.State == HarnessRunState.Created && attempt.AttemptNumber > 1 && continuations is not null)
                await continuations.ContinueAsync(owner.RootRunId, owner.AgentId, ct).ConfigureAwait(false);
            var scope = await scopes.GetAsync(runId, ct).ConfigureAwait(false)
                ?? throw new HarnessStoreException("subagent_scope_missing");
            var policy = PolicySnapshotMapper.FromWire(BrokerPolicyContracts.ReadSnapshot(JsonNode.Parse(scope.PolicySnapshotJson)!.AsObject()));
            if (!roots.TryGetRoot(parent.RunId, out var parentRoot) || parentRoot is null
                || !roots.TryGetPolicySnapshot(parent.RunId, out var parentPolicy) || parentPolicy is null) return false;
            if (!string.Equals(Path.TrimEndingDirectorySeparator(parentRoot), Path.TrimEndingDirectorySeparator(scope.ParentRoot), StringComparison.OrdinalIgnoreCase))
                throw new HarnessStoreException("subagent_scope_invalid");
            if (run.State == HarnessRunState.Running && (run.ModelStepsInWindow != 0 || run.ToolCallsInWindow != 0))
            {
                await new HarnessRecoveryScanner(events, time).ScanRunAsync(runId, ct).ConfigureAwait(false);
                run = (await events.GetRunAsync(runId, ct).ConfigureAwait(false))!;
                if (run.State.IsTerminal()) return true;
            }
            if (run.State is HarnessRunState.WaitingUser or HarnessRunState.WaitingSubagents)
                throw new HarnessStoreException("subagent_recovery_wait_unsupported");
            if (run.State is HarnessRunState.Created or HarnessRunState.Running)
                await new SqliteModelContextSource(options, payloads).LoadForSubagentContinuationAsync(runId, ct).ConfigureAwait(false);
            var actual = await new Workspace.WindowsWorkspaceIdentityResolver().ResolveAsync(scope.WorkspaceRoot, ct).ConfigureAwait(false);
            if (actual != policy.Workspace) throw new HarnessStoreException("subagent_workspace_identity_changed");
            await SubagentParentLeaseRestorer.EnsureAsync(gateways, parent, parentRoot, parentPolicy, time.UtcNow, ct).ConfigureAwait(false);
            // Created remains queued: registering it here would bypass admission
            // and make the scheduler skip its durable Created -> Running commit.
            if (run.State != HarnessRunState.Created) roots.Register(runId, scope.WorkspaceRoot, policy);
            return true;
        }
        catch (HarnessStoreException error)
        {
            await EndAsync(runId, false, error.Code, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is AgentProtocolException or JsonException or ArgumentException)
        {
            await EndAsync(runId, false, "subagent_scope_invalid", ct).ConfigureAwait(false);
            return true;
        }
    }

    private async Task EndAsync(RunId runId, bool canceled, string code, CancellationToken ct)
    {
        for (var retry = 0; retry < 3; retry++)
        {
            var run = await events.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (run is null || run.State.IsTerminal()) return;
            var commit = SubagentAdmission.CreateTerminalCommit(run, new("evt_" + Guid.NewGuid().ToString("N")), canceled, code, time.UtcNow);
            var result = await events.CommitAsync(commit, ct).ConfigureAwait(false);
            if (result.Outcome == HarnessCommitOutcome.Committed) return;
            if (result.Outcome != HarnessCommitOutcome.VersionConflict) break;
        }
        throw new HarnessStoreException("subagent_recovery_conflict");
    }
}
