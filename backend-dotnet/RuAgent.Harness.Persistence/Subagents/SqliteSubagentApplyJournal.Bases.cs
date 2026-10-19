using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;
using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Persistence.Subagents;

public sealed partial class SqliteSubagentApplyJournal
{
    /// <summary>Only durable receipts advance a frozen base; uncertainty requires an explicit user resolution.</summary>
    public async Task<IReadOnlyDictionary<string, string>> ReadAppliedBaseHashesAsync(
        string rootRunId, string agentId, long beforeResultVersion, CancellationToken ct)
    {
        if (beforeResultVersion < 1 || beforeResultVersion > SubagentLimits.MaxAttempts)
            throw new HarnessStoreException("subagent_apply_identity_invalid");
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: true);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Validate ownership even when no earlier result can exist.
        if (beforeResultVersion == 1)
            _ = await ReadAsync(connection, transaction, rootRunId, agentId, 1, ct).ConfigureAwait(false);
        for (var version = 1L; version < beforeResultVersion; version++)
        {
            var state = await ReadAsync(connection, transaction, rootRunId, agentId, version, ct).ConfigureAwait(false);
            if (state is null) continue;
            var uncertain = state.Outcome.Status is SubagentApplyStatus.Pending or SubagentApplyStatus.Unknown;
            if (uncertain && !await HasResolutionAsync(connection, transaction, rootRunId, agentId, version, ct).ConfigureAwait(false))
                throw new HarnessStoreException("subagent_apply_reconciliation_required");
            if (!uncertain && state.Outcome.Status is not (SubagentApplyStatus.Applied or SubagentApplyStatus.Partial)) continue;
            foreach (var file in state.Files.Where(f => f.Receipted)) hashes[file.RelativePath] = file.ProposedHash;
            if (hashes.Count > SubagentLimits.MaxSnapshotFiles)
                throw new HarnessStoreException("subagent_integration_base_too_large");
        }
        return new ReadOnlyDictionary<string, string>(hashes);
    }

    internal static async Task<bool> HasResolutionAsync(SqliteConnection connection, SqliteTransaction transaction,
        string root, string agent, long version, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(SELECT 1 FROM agent_subagent_apply_resolutions a JOIN agent_events e
                ON e.event_id=a.resolution_event_id AND e.run_id=a.root_run_id AND e.kind='reconciliation.resolved'
                    AND e.safe_code=a.decision AND e.model_step_id=a.model_step_id AND e.tool_call_id=a.tool_call_id
                    AND e.execution_id=a.execution_id
                WHERE a.root_run_id=$root AND a.agent_id=$agent AND a.result_version=$version
                    AND a.decision IN ('applied','not_applied','unknown_continue'))
            """;
        command.Parameters.AddWithValue("$root", root);
        command.Parameters.AddWithValue("$agent", agent);
        command.Parameters.AddWithValue("$version", version);
        return (long)(await command.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L) == 1;
    }
}
