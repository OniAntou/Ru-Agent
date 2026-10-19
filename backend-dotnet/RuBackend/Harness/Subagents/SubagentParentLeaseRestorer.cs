using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Policy;
using RuAgent.Harness.Persistence;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Policy;
using RuBackend.Harness.Workspace;

namespace RuBackend.Harness.Subagents;

public static class SubagentParentLeaseRestorer
{
    public static async Task EnsureAsync(IToolGatewayFactory gateways, AgentRun parent, string root,
        PolicySnapshot policy, DateTimeOffset now, CancellationToken ct)
    {
        if (parent.State.IsTerminal() || parent.WorkspaceId != policy.Workspace.WorkspaceId
            || parent.PolicySnapshotVersion != policy.Version || parent.PolicySnapshotSha256 != policy.Sha256
            || parent.Preset != policy.Preset)
            throw new HarnessStoreException("subagent_parent_policy_changed");
        var actual = await new WindowsWorkspaceIdentityResolver().ResolveAsync(root, ct).ConfigureAwait(false);
        if (actual != policy.Workspace) throw new HarnessStoreException("subagent_parent_workspace_identity_changed");
        var effectiveParent = SessionApprovalLedger.Apply(policy, parent.SessionId.Value, now)!;
        await gateways.CreateAsync(root, effectiveParent, parent.RunId, parent.SessionId.Value, ct).ConfigureAwait(false);
    }
}
