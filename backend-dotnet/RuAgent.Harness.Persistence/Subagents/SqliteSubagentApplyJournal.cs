using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentApplyJournal(HarnessSqliteOptions options, WorkspaceJournalProtector protector) : ISubagentApplyJournal, ISubagentApplyJournalReader
{
    public async Task<SubagentApplyOutcome?> BeginAsync(SubagentChangeSet changes, string fingerprint, CancellationToken ct)
    {
        if (fingerprint.Length != 64 || fingerprint.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new HarnessStoreException("subagent_apply_fingerprint_invalid");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        _ = await WorkspaceAsync(connection, transaction, changes, ct).ConfigureAwait(false);
        using var command = Command(connection, transaction, changes);
        command.CommandText = "SELECT fingerprint,status,applied_files,code FROM agent_subagent_apply_operations WHERE root_run_id=$root AND agent_id=$agent AND result_version=$version";
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetString(0) != fingerprint) throw new HarnessStoreException("subagent_apply_result_conflict");
                return new((SubagentApplyStatus)reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3));
            }
        }
        command.CommandText = """
            INSERT INTO agent_subagent_apply_operations(root_run_id,agent_id,result_version,fingerprint,status,code)
            VALUES($root,$agent,$version,$fingerprint,0,'pending')
            """;
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return null;
    }

    public async Task RecordFileIntentAsync(SubagentChangeSet changes, int fileIndex, CancellationToken ct)
    {
        if (fileIndex < 0 || fileIndex >= changes.Files.Count) throw new HarnessStoreException("subagent_apply_file_invalid");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        var workspace = await WorkspaceAsync(connection, transaction, changes, ct).ConfigureAwait(false);
        var file = changes.Files[fileIndex];
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { file.RelativePath, file.BaseHash, file.ProposedHash, file.Mode });
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{changes.ParentRunId}:{changes.AgentId}:{changes.ResultVersion}:{fileIndex}"))).ToLowerInvariant();
        var encrypted = await protector.EncryptAsync(workspace, "apply_" + identity, "subagent.apply", metadata, ct).ConfigureAwait(false);
        using var command = Command(connection, transaction, changes);
        command.CommandText = """
            INSERT INTO agent_subagent_apply_files(root_run_id,agent_id,result_version,file_index,protection_version,nonce,ciphertext,authentication_tag)
            SELECT $root,$agent,$version,$index,$protection,$nonce,$ciphertext,$tag
            FROM agent_subagent_apply_operations WHERE root_run_id=$root AND agent_id=$agent AND result_version=$version AND status=0
            """;
        command.Parameters.AddWithValue("$index", fileIndex);
        command.Parameters.AddWithValue("$protection", encrypted.ProtectionVersion);
        command.Parameters.AddWithValue("$nonce", encrypted.Nonce.ToArray());
        command.Parameters.AddWithValue("$ciphertext", encrypted.Ciphertext.ToArray());
        command.Parameters.AddWithValue("$tag", encrypted.AuthenticationTag.ToArray());
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new HarnessStoreException("subagent_apply_not_pending");
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordFileReceiptAsync(SubagentChangeSet changes, int fileIndex, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = Command(connection, transaction, changes);
        command.CommandText = """
            UPDATE agent_subagent_apply_files SET receipted=1
            WHERE root_run_id=$root AND agent_id=$agent AND result_version=$version AND file_index=$index
            AND EXISTS(SELECT 1 FROM agent_subagent_apply_operations WHERE root_run_id=$root AND agent_id=$agent AND result_version=$version AND status=0)
            """;
        command.Parameters.AddWithValue("$index", fileIndex);
        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) throw new HarnessStoreException("subagent_apply_receipt_missing");
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task FinishAsync(SubagentChangeSet changes, SubagentApplyOutcome outcome, CancellationToken ct)
    {
        if (!Enum.IsDefined(outcome.Status) || outcome.Status == SubagentApplyStatus.Pending || outcome.AppliedFiles < 0
            || outcome.AppliedFiles > changes.Files.Count || string.IsNullOrEmpty(outcome.Code) || outcome.Code.Length > 128
            || outcome.Code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new HarnessStoreException("subagent_apply_outcome_invalid");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var command = Command(connection, null, changes);
        command.CommandText = """
            UPDATE agent_subagent_apply_operations SET status=$status,applied_files=$files,code=$code
            WHERE root_run_id=$root AND agent_id=$agent AND result_version=$version AND status=0
            """;
        command.Parameters.AddWithValue("$status", (int)outcome.Status);
        command.Parameters.AddWithValue("$files", outcome.AppliedFiles);
        command.Parameters.AddWithValue("$code", outcome.Code);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, SubagentChangeSet changes)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", changes.ParentRunId);
        command.Parameters.AddWithValue("$agent", changes.AgentId);
        command.Parameters.AddWithValue("$version", changes.ResultVersion);
        return command;
    }

    private static async Task<WorkspaceId> WorkspaceAsync(SqliteConnection connection, SqliteTransaction transaction,
        SubagentChangeSet changes, CancellationToken ct)
    {
        using var command = Command(connection, transaction, changes);
        command.CommandText = "SELECT r.workspace_id FROM agent_runs r JOIN agent_subagents s ON s.root_run_id=r.run_id WHERE r.run_id=$root AND s.agent_id=$agent";
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string workspace
            ? new WorkspaceId(workspace) : throw new HarnessStoreException("subagent_apply_ownership_denied");
    }
}
