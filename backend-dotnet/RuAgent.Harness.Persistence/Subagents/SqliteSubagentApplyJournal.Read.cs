using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentApplyJournal
{
    public async Task<SubagentApplyJournalState?> ReadAsync(string rootRunId, string agentId, long resultVersion, CancellationToken ct)
    {
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        return await ReadAsync(connection, transaction, rootRunId, agentId, resultVersion, ct).ConfigureAwait(false);
    }

    private async Task<SubagentApplyJournalState?> ReadAsync(SqliteConnection connection, SqliteTransaction transaction,
        string rootRunId, string agentId, long resultVersion, CancellationToken ct)
    {
        if (!SubagentContractValidator.IsValidId(rootRunId) || !SubagentContractValidator.IsValidId(agentId)
            || resultVersion < 1 || resultVersion > SubagentLimits.MaxAttempts)
            throw new HarnessStoreException("subagent_apply_identity_invalid");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$root", rootRunId);
        command.Parameters.AddWithValue("$agent", agentId);
        command.Parameters.AddWithValue("$version", resultVersion);
        command.CommandText = "SELECT r.workspace_id FROM agent_runs r JOIN agent_subagents s ON s.root_run_id=r.run_id WHERE r.run_id=$root AND s.agent_id=$agent";
        var workspace = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string
            ?? throw new HarnessStoreException("subagent_apply_ownership_denied");
        command.CommandText = "SELECT fingerprint,status,applied_files,code FROM agent_subagent_apply_operations WHERE root_run_id=$root AND agent_id=$agent AND result_version=$version";
        string fingerprint;
        SubagentApplyOutcome outcome;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
            fingerprint = reader.GetString(0);
            outcome = new((SubagentApplyStatus)reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3));
        }
        if (!IsHash(fingerprint) || !Enum.IsDefined(outcome.Status) || outcome.AppliedFiles < 0
            || outcome.AppliedFiles > SubagentLimits.MaxSnapshotFiles || string.IsNullOrEmpty(outcome.Code)
            || outcome.Code.Length > 128 || outcome.Code.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            throw new HarnessStoreException("subagent_apply_journal_invalid");
        command.CommandText = """
            SELECT file_index,protection_version,nonce,ciphertext,authentication_tag,receipted
            FROM agent_subagent_apply_files WHERE root_run_id=$root AND agent_id=$agent AND result_version=$version
            ORDER BY file_index LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", SubagentLimits.MaxSnapshotFiles + 1);
        using var rows = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var files = new List<SubagentApplyFileIntent>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await rows.ReadAsync(ct).ConfigureAwait(false))
        {
            var index = rows.GetInt32(0);
            if (index != files.Count || files.Count >= SubagentLimits.MaxSnapshotFiles)
                throw new HarnessStoreException("subagent_apply_journal_invalid");
            var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{rootRunId}:{agentId}:{resultVersion}:{index}"))).ToLowerInvariant();
            var bytes = await protector.DecryptAsync(new WorkspaceId(workspace), "apply_" + identity, "subagent.apply",
                new(rows.GetInt32(1), (byte[])rows[2], (byte[])rows[3], (byte[])rows[4]), ct).ConfigureAwait(false);
            FileMetadata metadata;
            try { metadata = JsonSerializer.Deserialize<FileMetadata>(bytes.Span) ?? throw new JsonException(); }
            catch (JsonException error) { throw new HarnessStoreException("subagent_apply_journal_invalid", error); }
            if (!IsRelativePath(metadata.RelativePath) || !paths.Add(metadata.RelativePath)
                || !IsFileHash(metadata.BaseHash) || !IsFileHash(metadata.ProposedHash) || metadata.Mode is not (100644 or 100755))
                throw new HarnessStoreException("subagent_apply_journal_invalid");
            files.Add(new(index, metadata.RelativePath, metadata.BaseHash, metadata.ProposedHash, metadata.Mode, rows.GetInt32(5) == 1));
        }
        if (outcome.AppliedFiles > files.Count || outcome.Status == SubagentApplyStatus.Applied
            && (outcome.AppliedFiles != files.Count || files.Any(f => !f.Receipted)))
            throw new HarnessStoreException("subagent_apply_journal_invalid");
        return new(fingerprint, outcome, files.AsReadOnly());
    }

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);
    private static bool IsFileHash(string? value) => value == "missing" || IsHash(value);
    private static bool IsRelativePath(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 4096
        && !Path.IsPathRooted(value) && !value.Contains(':') && value.Split(['/', '\\']).All(p =>
            p is not ("" or "." or "..") && !p.Equals(".git", StringComparison.OrdinalIgnoreCase)
            && !p.EndsWith('.') && !p.EndsWith(' '));
    private sealed record FileMetadata(string RelativePath, string BaseHash, string ProposedHash, int Mode);
}
