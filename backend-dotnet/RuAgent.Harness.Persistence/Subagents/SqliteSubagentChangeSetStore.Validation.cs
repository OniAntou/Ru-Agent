using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentChangeSetStore
{
    private static void Validate(SubagentChangeSet changes)
    {
        if (!SubagentContractValidator.IsValidId(changes.AgentId) || !SubagentContractValidator.IsValidId(changes.ParentRunId)
            || !SubagentContractValidator.IsValidId(changes.SnapshotId) || !Path.IsPathFullyQualified(changes.ParentRoot)
            || changes.ResultVersion < 1 || changes.ResultVersion > SubagentLimits.MaxAttempts
            || changes.Files.Count > SubagentLimits.MaxSnapshotFiles) throw new HarnessStoreException("subagent_changes_invalid");
        long total = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in changes.Files)
        {
            total += file.Content.Length;
            if (file.Content.Length > MaximumFileBytes || total > SubagentLimits.MaxSnapshotBytes)
                throw new HarnessStoreException("subagent_changes_too_large");
            if (string.IsNullOrWhiteSpace(file.RelativePath) || file.RelativePath.Length > 4096
                || Path.IsPathRooted(file.RelativePath) || !paths.Add(file.RelativePath)
                || file.RelativePath.Split(['/', '\\']).Any(p => p is "" or "." or ".."
                    || p.Equals(".git", StringComparison.OrdinalIgnoreCase) || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || p.EndsWith('.') || p.EndsWith(' '))
                || !IsFileHash(file.BaseHash) || !IsFileHash(file.ProposedHash) || file.Mode is not (100644 or 100755)
                || (file.ProposedHash == "missing" ? file.Content.Length != 0 : Hash(file.Content.Span) != file.ProposedHash))
                throw new HarnessStoreException("subagent_changes_invalid");
        }
    }

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool IsFileHash(string? value) => value == "missing" || value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);

    private static async Task<Owner> ReadOwnerAsync(SqliteConnection connection, SqliteTransaction transaction, RunId childRunId, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT s.root_run_id,root.workspace_id,s.agent_id,a.attempt_number,s.role,child.state
            FROM agent_subagent_attempts a JOIN agent_subagents s ON s.agent_id=a.agent_id
            JOIN agent_runs root ON root.run_id=s.root_run_id JOIN agent_runs child ON child.run_id=a.run_id WHERE a.run_id=$run
            """;
        command.Parameters.AddWithValue("$run", childRunId.Value);
        using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new HarnessStoreException("subagent_changes_not_found");
        return new(reader.GetString(0), new(reader.GetString(1)), reader.GetString(2), reader.GetInt32(3),
            (SubagentRole)reader.GetInt32(4), (HarnessRunState)reader.GetInt32(5));
    }

    private sealed record Owner(string RootId, WorkspaceId WorkspaceId, string AgentId, int Attempt, SubagentRole Role, HarnessRunState State);
}
