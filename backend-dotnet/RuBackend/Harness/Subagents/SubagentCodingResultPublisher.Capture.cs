using System.Text.Json.Nodes;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Subagents;
using RuAgent.Harness.Persistence;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol.Subagents;
using RuBackend.Harness.Broker;
using RuBackend.Harness.Workspace;

namespace RuBackend.Harness.Subagents;

public sealed partial class SubagentCodingResultPublisher
{
    private async Task CaptureAsync(AgentRun run, SubagentRecord owner, long version, CancellationToken ct)
    {
        var scope = await scopes.GetAsync(run.RunId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("subagent_scope_missing");
        var policy = PolicySnapshotMapper.FromWire(BrokerPolicyContracts.ReadSnapshot(
            JsonNode.Parse(scope.PolicySnapshotJson)!.AsObject()));
        using var identityLease = DirectoryHandleLease.Open(scope.WorkspaceRoot, requireCanonicalPath: true);
        var actual = await new WindowsWorkspaceIdentityResolver().ResolveAsync(scope.WorkspaceRoot, ct).ConfigureAwait(false);
        if (actual != policy.Workspace) throw new HarnessStoreException("subagent_workspace_identity_changed");
        var bases = await journal.ReadAppliedBaseHashesAsync(owner.RootRunId.Value, owner.AgentId, version, ct).ConfigureAwait(false);
        var source = new SubagentCollectionSource(owner.RootRunId.Value, run.RunId.Value, run.SessionId.Value, owner.AgentId,
            scope.ParentRoot, scope.WorkspaceRoot, storage.Root, scope.WorktreeManifestJson!);
        var frozen = await gateways.CollectSubagentChangesAsync(source, version, bases, ct).ConfigureAwait(false);
        if (frozen.ParentRunId != owner.RootRunId.Value || frozen.AgentId != owner.AgentId || frozen.ResultVersion != version
            || !string.Equals(frozen.ParentRoot, scope.ParentRoot, StringComparison.OrdinalIgnoreCase))
            throw new HarnessStoreException("subagent_scope_invalid");
        await changes.SaveAsync(run.RunId, frozen, ct).ConfigureAwait(false);
    }
}
