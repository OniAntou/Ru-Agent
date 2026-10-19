using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentChangeSetStore
{
    public async Task<SubagentChangeSet?> GetAsync(RunId rootId, string agentId, RunId childRunId, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var owner = await ReadOwnerAsync(connection, transaction, childRunId, ct).ConfigureAwait(false);
        if (owner.RootId != rootId.Value || owner.AgentId != agentId || owner.Role != SubagentRole.Coding)
            throw new HarnessStoreException("subagent_changes_ownership_denied");
        var manifest = await ReadManifestAsync(connection, transaction, childRunId, owner.WorkspaceId, ct).ConfigureAwait(false);
        if (manifest is null) return null;
        if (manifest.ParentRunId != rootId.Value || manifest.AgentId != agentId || manifest.ResultVersion != owner.Attempt
            || manifest.Files is null || manifest.Files.Length > SubagentLimits.MaxSnapshotFiles)
            throw new HarnessStoreException("subagent_changes_invalid");
        long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || file.Length < 0 || file.Length > MaximumFileBytes) throw new HarnessStoreException("subagent_changes_invalid");
            total += file.Length;
            if (total > SubagentLimits.MaxSnapshotBytes) throw new HarnessStoreException("subagent_changes_too_large");
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT file_index,chunk_index,protection_version,nonce,ciphertext,authentication_tag
            FROM agent_subagent_change_chunks WHERE run_id=$run ORDER BY file_index,chunk_index
            """;
        command.Parameters.AddWithValue("$run", childRunId.Value);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var hasChunk = await reader.ReadAsync(ct).ConfigureAwait(false);
        var files = new List<SubagentFileChange>();
        for (var fileIndex = 0; fileIndex < manifest.Files.Length; fileIndex++)
        {
            var metadata = manifest.Files[fileIndex];
            var content = new byte[metadata.Length];
            for (var offset = 0; offset < content.Length; offset += ChunkBytes)
            {
                var chunk = offset / ChunkBytes;
                if (!hasChunk || reader.GetInt32(0) != fileIndex || reader.GetInt32(1) != chunk)
                    throw new HarnessStoreException("subagent_changes_chunk_missing");
                var bytes = await protector.DecryptAsync(owner.WorkspaceId, ChunkIdentity(childRunId, fileIndex, chunk),
                    "subagent.content", new(reader.GetInt32(2), (byte[])reader[3], (byte[])reader[4], (byte[])reader[5]), ct).ConfigureAwait(false);
                var expected = Math.Min(ChunkBytes, content.Length - offset);
                if (bytes.Length != expected) throw new HarnessStoreException("subagent_changes_chunk_invalid");
                bytes.CopyTo(content.AsMemory(offset, expected));
                hasChunk = await reader.ReadAsync(ct).ConfigureAwait(false);
            }
            files.Add(new(metadata.RelativePath, metadata.BaseHash, metadata.ProposedHash, content, metadata.Mode));
        }
        if (hasChunk) throw new HarnessStoreException("subagent_changes_chunk_invalid");
        var changes = new SubagentChangeSet(manifest.AgentId, manifest.ParentRunId, manifest.ParentRoot,
            manifest.SnapshotId, manifest.ResultVersion, files.AsReadOnly());
        Validate(changes);
        return changes;
    }

    private async Task<Manifest?> ReadManifestAsync(SqliteConnection connection, SqliteTransaction transaction,
        RunId childRunId, WorkspaceId workspaceId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT protection_version,nonce,ciphertext,authentication_tag FROM agent_subagent_change_sets WHERE run_id=$run";
        command.Parameters.AddWithValue("$run", childRunId.Value);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var bytes = await protector.DecryptAsync(workspaceId, childRunId.Value, "subagent.changes",
            new(reader.GetInt32(0), (byte[])reader[1], (byte[])reader[2], (byte[])reader[3]), ct).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<Manifest>(bytes.Span) ?? throw new JsonException(); }
        catch (JsonException error) { throw new HarnessStoreException("subagent_changes_invalid", error); }
    }
}
