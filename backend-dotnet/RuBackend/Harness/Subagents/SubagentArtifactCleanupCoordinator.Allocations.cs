using System.Text.Json;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;

namespace RuBackend.Harness.Subagents;

public sealed partial class SubagentArtifactCleanupCoordinator
{
    private async Task<IReadOnlyDictionary<string, SubagentWorkspaceManifest>> ReclaimAllocationsAsync(RunId root, CancellationToken ct)
    {
        var reclaimed = new Dictionary<string, SubagentWorkspaceManifest>(StringComparer.Ordinal);
        foreach (var evidence in await allocations.ListAsync(root, ct).ConfigureAwait(false))
        {
            try
            {
                await gateways.ReclaimSubagentAllocationAsync(evidence, storage.Root, ct).ConfigureAwait(false);
                if (evidence.WorktreeManifestJson is not null)
                    reclaimed.Add(evidence.AgentId, JsonSerializer.Deserialize<SubagentWorkspaceManifest>(evidence.WorktreeManifestJson)
                        ?? throw new HarnessStoreException("subagent_cleanup_scope_invalid"));
            }
            catch (Exception error) when (error is RuAgent.Protocol.AgentProtocolException or SubagentWorkspaceException
                or ToolTransportException or JsonException)
            {
                throw new HarnessStoreException(error is SubagentWorkspaceException workspace ? workspace.Code
                    : error is RuAgent.Protocol.AgentProtocolException protocol ? protocol.Code
                    : error is ToolTransportException ? "subagent_cleanup_unavailable" : "subagent_cleanup_scope_invalid");
            }
        }
        return reclaimed;
    }
}
