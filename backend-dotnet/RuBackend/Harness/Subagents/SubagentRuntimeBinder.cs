using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Policy;
using RuAgent.Protocol.Harness;
using System.Text.Json.Nodes;

namespace RuBackend.Harness.Subagents;

/// <summary>Rebinds only the persisted child ceiling before the shared coordinator can dispatch.</summary>
public sealed class SubagentRuntimeBinder(IHarnessEventStore events, SqliteSubagentStore agents,
    SqliteSubagentScopeStore scopes, IToolGatewayFactory gateways)
{
    public async Task<bool> BindAsync(RunId runId, string workspaceRoot, PolicySnapshot policy, CancellationToken ct)
    {
        var run = await events.GetRunAsync(runId, ct).ConfigureAwait(false);
        if (run is null) return false;
        var owner = await agents.FindOwnerAsync(run.SessionId, ct).ConfigureAwait(false);
        if (owner is null) return false;
        var attempt = await agents.GetLatestAttemptAsync(owner.RootRunId, owner.AgentId, ct).ConfigureAwait(false);
        var root = await events.GetRunAsync(owner.RootRunId, ct).ConfigureAwait(false);
        if (owner.Canceled || attempt?.Run.RunId != runId || run.State.IsTerminal() || root is null || root.State.IsTerminal())
            throw new HarnessStoreException("subagent_root_inactive");
        var scope = await scopes.GetAsync(runId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_scope_missing");
        var originalPolicy = PolicySnapshotMapper.FromWire(BrokerPolicyContracts.ReadSnapshot(
            JsonNode.Parse(scope.PolicySnapshotJson)!.AsObject()));
        var effectivePolicy = SessionApprovalLedger.Apply(originalPolicy, run.SessionId.Value, DateTimeOffset.UtcNow)!;
        if (!string.Equals(scope.WorkspaceRoot, Path.GetFullPath(workspaceRoot), StringComparison.OrdinalIgnoreCase)
            || policy.Version != effectivePolicy.Version || policy.Sha256 != effectivePolicy.Sha256
            || policy.Workspace.WorkspaceId != run.WorkspaceId)
            throw new HarnessStoreException("subagent_scope_invalid");
        var currentIdentity = await new Workspace.WindowsWorkspaceIdentityResolver().ResolveAsync(workspaceRoot, ct).ConfigureAwait(false);
        if (currentIdentity != policy.Workspace) throw new HarnessStoreException("subagent_workspace_identity_changed");
        var parentCeiling = BrokerPolicyContracts.ReadSnapshot(JsonNode.Parse(scope.ParentPolicySnapshotJson)!.AsObject());
        await gateways.BindDelegationAsync(owner.RootRunId, runId, owner.Role, scope.ParentPolicy, ct, parentCeiling).ConfigureAwait(false);
        return true;
    }
}
