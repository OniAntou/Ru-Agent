using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentWorkspaceAllocationStore
{
    public async Task RecordAsync(SubagentWorkspaceAllocation evidence, CancellationToken ct)
    {
        var bytes = Validate(evidence);
        if (evidence.Stage == SubagentWorkspaceAllocationStage.Reserved)
            throw new HarnessStoreException("subagent_allocation_transition_invalid");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var root = await RequireActiveRootAsync(connection, transaction, new(evidence.RootRunId), ct).ConfigureAwait(false);
        var previous = (await ReadAsync(connection, transaction, root, ct).ConfigureAwait(false))
            .SingleOrDefault(row => row.AgentId == evidence.AgentId)
            ?? throw new HarnessStoreException("subagent_allocation_missing");
        if (previous == evidence) return;
        if (previous.SnapshotId != evidence.SnapshotId
            || previous.SnapshotJson is not null && previous.SnapshotJson != evidence.SnapshotJson
            || previous.WorktreeManifestJson is not null && previous.WorktreeManifestJson != evidence.WorktreeManifestJson)
            throw new HarnessStoreException("subagent_allocation_conflict");
        if ((int)evidence.Stage != (int)previous.Stage + 1)
            throw new HarnessStoreException("subagent_allocation_transition_invalid");
        var encrypted = await Protector.EncryptAsync(root.WorkspaceId, ProtectionId(evidence.RootRunId, evidence.AgentId),
            "subagent.allocation", bytes, ct).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_subagent_workspace_allocations SET stage=$stage,protection_version=$version,
                nonce=$nonce,ciphertext=$ciphertext,authentication_tag=$tag
            WHERE root_run_id=$root AND agent_id=$agent AND snapshot_id=$snapshot AND stage=$previous
            """;
        command.Parameters.AddWithValue("$root", evidence.RootRunId);
        command.Parameters.AddWithValue("$agent", evidence.AgentId);
        command.Parameters.AddWithValue("$snapshot", evidence.SnapshotId);
        command.Parameters.AddWithValue("$stage", (int)evidence.Stage);
        command.Parameters.AddWithValue("$previous", (int)previous.Stage);
        command.Parameters.AddWithValue("$version", encrypted.ProtectionVersion);
        command.Parameters.AddWithValue("$nonce", encrypted.Nonce.ToArray());
        command.Parameters.AddWithValue("$ciphertext", encrypted.Ciphertext.ToArray());
        command.Parameters.AddWithValue("$tag", encrypted.AuthenticationTag.ToArray());
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            throw new HarnessStoreException("subagent_allocation_conflict");
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    private static byte[] Validate(SubagentWorkspaceAllocation evidence)
    {
        if (!SubagentContractValidator.IsValidId(evidence.RootRunId) || !SubagentContractValidator.IsValidId(evidence.AgentId)
            || !SubagentContractValidator.IsValidId(evidence.SnapshotId) || !evidence.SnapshotId.StartsWith("snapshot_", StringComparison.Ordinal)
            || !Enum.IsDefined(evidence.Stage)
            || (evidence.Stage == SubagentWorkspaceAllocationStage.Reserved) != (evidence.SnapshotJson is null)
            || (evidence.Stage >= SubagentWorkspaceAllocationStage.WorktreeRegistered) != (evidence.WorktreeManifestJson is not null))
            throw new HarnessStoreException("subagent_allocation_invalid");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence);
        if (bytes.Length > SubagentWorkspaceAllocation.MaximumEvidenceBytes)
            throw new HarnessStoreException("subagent_allocation_evidence_too_large");
        try
        {
            foreach (var json in new[] { evidence.SnapshotJson, evidence.WorktreeManifestJson })
            {
                if (json is null) continue;
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            }
        }
        catch (JsonException error) { throw new HarnessStoreException("subagent_allocation_invalid", error); }
        return bytes;
    }

    private static string ProtectionId(string root, string agent) => "allocation_" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root + ":" + agent))).ToLowerInvariant();
}
