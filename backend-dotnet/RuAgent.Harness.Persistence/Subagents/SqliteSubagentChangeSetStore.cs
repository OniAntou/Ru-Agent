using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

/// <summary>Encrypted immutable coding outputs. Storage never grants authority to apply them.</summary>
public sealed partial class SqliteSubagentChangeSetStore(HarnessSqliteOptions options, WorkspaceJournalProtector protector)
{
    private const int ChunkBytes = 1024 * 1024;
    private const int MaximumFileBytes = 32 * ChunkBytes;

    public async Task SaveAsync(RunId childRunId, SubagentChangeSet changes, CancellationToken ct)
    {
        // Freeze caller-owned buffers before hashing or awaiting storage.
        if (changes.Files.Count > SubagentLimits.MaxSnapshotFiles) throw new HarnessStoreException("subagent_changes_too_large");
        var copies = new List<SubagentFileChange>();
        long totalBytes = 0;
        foreach (var file in changes.Files)
        {
            totalBytes += file.Content.Length;
            if (copies.Count >= SubagentLimits.MaxSnapshotFiles || file.Content.Length > MaximumFileBytes
                || totalBytes > SubagentLimits.MaxSnapshotBytes) throw new HarnessStoreException("subagent_changes_too_large");
            copies.Add(file with { Content = file.Content.ToArray() });
        }
        var frozen = changes with { Files = copies.AsReadOnly() };
        Validate(frozen);
        var manifest = Manifest.From(frozen);
        var metadata = JsonSerializer.SerializeToUtf8Bytes(manifest);
        if (metadata.Length > WorkspaceJournalProtector.MaximumJournalBytes) throw new HarnessStoreException("subagent_changes_too_large");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var owner = await ReadOwnerAsync(connection, transaction, childRunId, ct).ConfigureAwait(false);
        if (owner.RootId != changes.ParentRunId || owner.AgentId != changes.AgentId || owner.Attempt != changes.ResultVersion
            || owner.Role != SubagentRole.Coding || owner.State != HarnessRunState.Completed)
            throw new HarnessStoreException("subagent_changes_ownership_denied");
        var previous = await ReadManifestAsync(connection, transaction, childRunId, owner.WorkspaceId, ct).ConfigureAwait(false);
        if (previous is not null)
        {
            if (!metadata.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(previous)))
                throw new HarnessStoreException("subagent_changes_conflict");
            return;
        }
        var encrypted = await protector.EncryptAsync(owner.WorkspaceId, childRunId.Value, "subagent.changes", metadata, ct).ConfigureAwait(false);
        using var command = Command(connection, transaction, childRunId, encrypted);
        command.CommandText = "INSERT INTO agent_subagent_change_sets(run_id,protection_version,nonce,ciphertext,authentication_tag) VALUES($run,$protection,$nonce,$ciphertext,$tag)";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        for (var fileIndex = 0; fileIndex < frozen.Files.Count; fileIndex++)
        {
            var content = frozen.Files[fileIndex].Content;
            for (var offset = 0; offset < content.Length; offset += ChunkBytes)
            {
                var chunk = offset / ChunkBytes;
                var bytes = content.Slice(offset, Math.Min(ChunkBytes, content.Length - offset));
                var envelope = await protector.EncryptAsync(owner.WorkspaceId, ChunkIdentity(childRunId, fileIndex, chunk),
                    "subagent.content", bytes, ct).ConfigureAwait(false);
                using var insert = Command(connection, transaction, childRunId, envelope);
                insert.CommandText = """
                    INSERT INTO agent_subagent_change_chunks(run_id,file_index,chunk_index,protection_version,nonce,ciphertext,authentication_tag)
                    VALUES($run,$file,$chunk,$protection,$nonce,$ciphertext,$tag)
                    """;
                insert.Parameters.AddWithValue("$file", fileIndex);
                insert.Parameters.AddWithValue("$chunk", chunk);
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction transaction, RunId run,
        EncryptedWorkspaceJournal encrypted)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$run", run.Value);
        command.Parameters.AddWithValue("$protection", encrypted.ProtectionVersion);
        command.Parameters.AddWithValue("$nonce", encrypted.Nonce.ToArray());
        command.Parameters.AddWithValue("$ciphertext", encrypted.Ciphertext.ToArray());
        command.Parameters.AddWithValue("$tag", encrypted.AuthenticationTag.ToArray());
        return command;
    }

    private static string ChunkIdentity(RunId run, int file, int chunk) => "delta_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{run.Value}:{file}:{chunk}"))).ToLowerInvariant();

    private sealed record FileEntry(string RelativePath, string BaseHash, string ProposedHash, int Length, int Mode);
    private sealed record Manifest(string AgentId, string ParentRunId, string ParentRoot, string SnapshotId,
        long ResultVersion, FileEntry[] Files)
    {
        public static Manifest From(SubagentChangeSet changes) => new(changes.AgentId, changes.ParentRunId,
            changes.ParentRoot, changes.SnapshotId, changes.ResultVersion, changes.Files.Select(f =>
                new FileEntry(f.RelativePath, f.BaseHash, f.ProposedHash, f.Content.Length, f.Mode)).ToArray());
    }
}
