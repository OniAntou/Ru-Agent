using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentWorkspaceAllocationStore
{
    public async Task<IReadOnlyList<SubagentWorkspaceAllocation>> ListAsync(RunId rootId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var root = await HarnessSqliteQueries.GetRunAsync(connection, transaction, rootId, ct).ConfigureAwait(false);
        return root is null ? [] : await ReadAsync(connection, transaction, root, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SubagentWorkspaceAllocation>> ReadAsync(SqliteConnection connection,
        SqliteTransaction transaction, AgentRun root, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT agent_id,snapshot_id,stage,protection_version,nonce,ciphertext,authentication_tag
            FROM agent_subagent_workspace_allocations WHERE root_run_id=$root ORDER BY agent_id LIMIT $limit
            """;
        command.Parameters.AddWithValue("$root", root.RunId.Value);
        command.Parameters.AddWithValue("$limit", SubagentLimits.MaxIdentities + 1);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var rows = new List<SubagentWorkspaceAllocation>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (rows.Count >= SubagentLimits.MaxIdentities) throw new HarnessStoreException("subagent_allocation_invalid");
            var agent = reader.GetString(0);
            var snapshot = reader.GetString(1);
            var stage = (SubagentWorkspaceAllocationStage)reader.GetInt32(2);
            SubagentWorkspaceAllocation evidence;
            if (stage == SubagentWorkspaceAllocationStage.Reserved)
                evidence = new(root.RunId.Value, agent, snapshot, stage);
            else
            {
                var envelope = new EncryptedWorkspaceJournal(reader.GetInt32(3),
                    HarnessSqliteMapper.ReadBoundedBlob(reader, 4, 12, "subagent_allocation_invalid", 12),
                    HarnessSqliteMapper.ReadBoundedBlob(reader, 5, SubagentWorkspaceAllocation.MaximumEvidenceBytes, "subagent_allocation_invalid"),
                    HarnessSqliteMapper.ReadBoundedBlob(reader, 6, 16, "subagent_allocation_invalid", 16));
                var bytes = await Protector.DecryptAsync(root.WorkspaceId, ProtectionId(root.RunId.Value, agent),
                    "subagent.allocation", envelope, ct).ConfigureAwait(false);
                try { evidence = JsonSerializer.Deserialize<SubagentWorkspaceAllocation>(bytes.Span) ?? throw new JsonException(); }
                catch (JsonException error) { throw new HarnessStoreException("subagent_allocation_invalid", error); }
            }
            _ = Validate(evidence);
            if (evidence.RootRunId != root.RunId.Value || evidence.AgentId != agent || evidence.SnapshotId != snapshot || evidence.Stage != stage)
                throw new HarnessStoreException("subagent_allocation_invalid");
            rows.Add(evidence);
        }
        return rows.AsReadOnly();
    }
}
