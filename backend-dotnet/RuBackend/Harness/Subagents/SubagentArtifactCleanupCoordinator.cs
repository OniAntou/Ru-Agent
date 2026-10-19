using System.Text.Json;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuAgent.Harness.Persistence.Subagents;
using RuBackend.Harness.Broker;

namespace RuBackend.Harness.Subagents;

public sealed partial class SubagentArtifactCleanupCoordinator(HarnessSqliteOptions options, IHarnessEventStore events,
    SqliteSubagentStore agents, SqliteSubagentScopeStore scopes, IToolGatewayFactory gateways, SubagentWorkspaceStorage storage,
    SqliteSubagentWorkspaceAllocationStore allocations)
{
    public async Task ReclaimSessionAsync(string sessionId, CancellationToken ct)
    {
        await RequireDeletionAsync(sessionId, null, ct).ConfigureAwait(false);
        string? after = null;
        while (true)
        {
            var roots = await agents.ListFamiliesForSessionAsync(new(sessionId), after, ct).ConfigureAwait(false);
            foreach (var root in roots)
            {
                await RequireDeletionAsync(sessionId, root, ct).ConfigureAwait(false);
                var children = new List<AgentRun>();
                foreach (var childId in await agents.ListOwnedRunIdsAsync(root, ct).ConfigureAwait(false))
                {
                    var child = await events.GetRunAsync(childId, ct).ConfigureAwait(false)
                        ?? throw new HarnessStoreException("subagent_cleanup_ownership_denied");
                    if (!child.State.IsTerminal()) throw new HarnessStoreException("subagent_cleanup_busy");
                    children.Add(child);
                }
                var reclaimedAllocations = await ReclaimAllocationsAsync(root, ct).ConfigureAwait(false);
                var reclaimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var child in children)
                {
                    var childId = child.RunId;
                    var scope = await scopes.GetRetainedForCleanupAsync(root, childId, ct).ConfigureAwait(false);
                    if (scope is null)
                    {
                        var owner = await agents.FindOwnerAsync(child.SessionId, ct).ConfigureAwait(false);
                        if (owner?.RootRunId == root && (owner.Role == RuAgent.Protocol.Subagents.SubagentRole.Review
                            || reclaimedAllocations.ContainsKey(owner.AgentId))) continue;
                        throw new HarnessStoreException("subagent_cleanup_scope_missing");
                    }
                    if (scope.WorktreeManifestJson is null)
                    {
                        if (!string.Equals(scope.ParentRoot, scope.WorkspaceRoot, StringComparison.OrdinalIgnoreCase))
                            throw new HarnessStoreException("subagent_cleanup_scope_invalid");
                        continue;
                    }
                    try
                    {
                        var manifest = JsonSerializer.Deserialize<SubagentWorkspaceManifest>(scope.WorktreeManifestJson)
                            ?? throw new HarnessStoreException("subagent_cleanup_scope_invalid");
                        if (reclaimedAllocations.TryGetValue(manifest.AgentId, out var allocated))
                        {
                            if (scope.ParentRoot != manifest.Snapshot.ParentRoot || scope.WorkspaceRoot != manifest.WorkspaceRoot
                                || JsonSerializer.Serialize(manifest with { Snapshot = manifest.Snapshot with { Files = [] } })
                                    != JsonSerializer.Serialize(allocated))
                                throw new HarnessStoreException("subagent_cleanup_scope_invalid");
                            continue;
                        }
                        if (!reclaimed.Add(scope.WorkspaceRoot)) continue;
                        await gateways.ReclaimSubagentWorkspaceAsync(new(root.Value, childId.Value, child.SessionId.Value,
                            manifest.AgentId, scope.ParentRoot, scope.WorkspaceRoot, storage.Root, scope.WorktreeManifestJson), ct).ConfigureAwait(false);
                    }
                    catch (Exception error) when (error is RuAgent.Protocol.AgentProtocolException or SubagentWorkspaceException
                        or ToolTransportException or JsonException)
                    {
                        throw new HarnessStoreException(error is SubagentWorkspaceException workspace ? workspace.Code
                            : error is RuAgent.Protocol.AgentProtocolException protocol ? protocol.Code
                            : error is ToolTransportException ? "subagent_cleanup_unavailable" : "subagent_cleanup_scope_invalid");
                    }
                }
            }
            if (roots.Count < SqliteSubagentStore.FamilyPageSize) return;
            after = roots[^1].Value;
        }
    }

    private async Task RequireDeletionAsync(string sessionId, RunId? root, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM agent_session_tombstones WHERE session_id=$session)";
        command.Parameters.AddWithValue("$session", sessionId);
        if (root is not null)
        {
            command.CommandText += " AND EXISTS(SELECT 1 FROM agent_subagent_families f JOIN agent_runs r ON r.run_id=f.root_run_id WHERE f.root_run_id=$root AND f.canceled=1 AND r.session_id=$session)";
            command.Parameters.AddWithValue("$root", root.Value.Value);
        }
        if ((long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) != 1)
            throw new HarnessStoreException("subagent_cleanup_not_authorized");
    }
}
