using System.Text.Json.Nodes;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Broker;

namespace RuBackend.Harness.Subagents;

/// <summary>Admission and deadline sweeps only; children use the existing global drive workers.</summary>
public sealed partial class SubagentScheduler(IHarnessEventStore events, SqliteSubagentStore agents,
    SqliteSubagentScopeStore scopes, IAgentRuntimeRootRegistry roots, ITimeProvider time,
    IToolGatewayFactory? gateways = null) : IDisposable
{
    private readonly SemaphoreSlim _tick = new(1, 1);
    private HashSet<string> _yieldingRuns = new(StringComparer.Ordinal);
    private string? _afterRootId;

    public bool ShouldYield(RunId runId) => Volatile.Read(ref _yieldingRuns).Contains(runId.Value);

    public async Task CancelChildrenBeforeReleaseAsync(RunId rootId, IAgentRuntimeRunCleanup cleanup, CancellationToken ct)
    {
        await CloseInternalSessionBeforeReleaseAsync(rootId, ct).ConfigureAwait(false);
        var owned = await agents.ListOwnedRunIdsAsync(rootId, ct).ConfigureAwait(false);
        if (owned.Count == 0) return;
        await agents.CancelFamilyAsync(rootId, ct).ConfigureAwait(false);
        foreach (var childRunId in owned)
        {
            var child = await events.GetRunAsync(childRunId, ct).ConfigureAwait(false);
            if (child is null || child.State.IsTerminal())
                await cleanup.CancelAndRemoveRunAsync(childRunId, ct).ConfigureAwait(false);
            else
                await TerminateAsync(childRunId, true, "subagent_parent_canceled", cleanup, ct).ConfigureAwait(false);
        }
    }

    public async Task TickAsync(IAgentRuntimeRunCleanup cleanup, CancellationToken ct)
    {
        await _tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var families = await agents.ListActiveFamiliesAsync(_afterRootId, ct).ConfigureAwait(false);
            if (families.Count == 0 && _afterRootId is not null)
                families = await agents.ListActiveFamiliesAsync(null, ct).ConfigureAwait(false);
            var yielding = new HashSet<string>(Volatile.Read(ref _yieldingRuns)
                .Where(id => roots.TryGetRoot(new RunId(id), out _)), StringComparer.Ordinal);
            for (var offset = 0; offset < families.Count; offset++)
            {
                var rootId = families[offset];
                yielding.Add(rootId.Value);
                var parent = await events.GetRunAsync(rootId, ct).ConfigureAwait(false);
                var inactive = parent is null || parent.State.IsTerminal();
                var parentAuthorityRestored = false;
                if (inactive && parent is not null) await agents.CancelFamilyAsync(rootId, ct).ConfigureAwait(false);
                foreach (var agent in await agents.ListAsync(rootId, ct).ConfigureAwait(false))
                {
                    var attempt = await agents.GetLatestAttemptAsync(rootId, agent.AgentId, ct).ConfigureAwait(false);
                    if (attempt is null || attempt.Run.State.IsTerminal()) continue;
                    yielding.Add(attempt.Run.RunId.Value);
                    var now = time.UtcNow;
                    var expired = attempt.AdmittedAt is null
                        ? now - attempt.CreatedAt >= SubagentLimits.AttemptTimeout
                        : attempt.DeadlineAt is null || now >= attempt.DeadlineAt;
                    if (inactive || agent.Canceled || expired)
                    {
                        await TerminateAsync(attempt.Run.RunId, inactive || agent.Canceled,
                            inactive || agent.Canceled ? "subagent_parent_canceled"
                                : attempt.AdmittedAt is null ? "subagent_admission_timeout" : "subagent_attempt_timeout", cleanup, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!roots.TryGetRoot(rootId, out var parentRoot) || parentRoot is null
                        || !roots.TryGetPolicySnapshot(rootId, out var parentPolicy) || parentPolicy is null
                        || parentPolicy.Sha256 != parent!.PolicySnapshotSha256) continue;
                    if (roots.TryGetRoot(attempt.Run.RunId, out _)) continue;
                    // An interrupted dispatched attempt belongs to recovery; never restart it here.
                    if (attempt.Run.State != HarnessRunState.Created
                        && (attempt.Run.State != HarnessRunState.Running || attempt.Run.ModelStepsInWindow != 0
                            || attempt.Run.ToolCallsInWindow != 0)) continue;
                    SubagentRuntimeScope? scope;
                    RuAgent.Harness.Core.Policy.PolicySnapshot childPolicy;
                    try
                    {
                        scope = await scopes.GetAsync(attempt.Run.RunId, ct).ConfigureAwait(false);
                        if (scope is null) continue; // preparation can still be committing
                        var wire = BrokerPolicyContracts.ReadSnapshot(JsonNode.Parse(scope.PolicySnapshotJson)!.AsObject());
                        childPolicy = PolicySnapshotMapper.FromWire(wire);
                    }
                    catch (Exception error) when (error is HarnessStoreException or AgentProtocolException or JsonException or ArgumentException)
                    {
                        await TerminateAsync(attempt.Run.RunId, false, "subagent_scope_invalid", cleanup, ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!parentAuthorityRestored && gateways is not null)
                    {
                        // A recovered waiting parent has no coordinator to open
                        // its lease. Restore that authority before a child binds.
                        try
                        {
                            await SubagentParentLeaseRestorer.EnsureAsync(gateways, parent!, parentRoot, parentPolicy, now, ct).ConfigureAwait(false);
                            parentAuthorityRestored = true;
                        }
                        catch (Exception error) when (error is AgentProtocolException or ToolTransportException or InvalidOperationException or HarnessStoreException)
                        {
                            await TerminateAsync(attempt.Run.RunId, false,
                                error is HarnessStoreException storeError ? storeError.Code : "subagent_parent_authority_unavailable", cleanup, ct).ConfigureAwait(false);
                            continue;
                        }
                    }
                    if (attempt.AdmittedAt is null && !await agents.TryAdmitAsync(agent.AgentId, now, ct).ConfigureAwait(false)) continue;
                    roots.Register(attempt.Run.RunId, scope.WorkspaceRoot, childPolicy);
                }
            }
            Volatile.Write(ref _yieldingRuns, yielding);
            _afterRootId = families.Count == SqliteSubagentStore.FamilyPageSize ? families[^1].Value : null;
        }
        finally { _tick.Release(); }
    }

    private async Task TerminateAsync(RunId runId, bool canceled, string code, IAgentRuntimeRunCleanup cleanup, CancellationToken ct)
    {
        await cleanup.CancelAndRemoveRunAsync(runId, ct).ConfigureAwait(false);
        for (var retry = 0; retry < 3; retry++)
        {
            var current = await events.GetRunAsync(runId, ct).ConfigureAwait(false);
            if (current is null || current.State.IsTerminal()) return;
            var commit = SubagentAdmission.CreateTerminalCommit(current,
                new RuAgent.Harness.Core.EventId("evt_" + Guid.NewGuid().ToString("N")), canceled, code, time.UtcNow);
            var result = await events.CommitAsync(commit, ct).ConfigureAwait(false);
            if (result.Outcome != HarnessCommitOutcome.VersionConflict) return;
        }
        // Cancellation already revoked dispatch. The next tick retries only the projection.
    }

    public void Dispose() => _tick.Dispose();
}
