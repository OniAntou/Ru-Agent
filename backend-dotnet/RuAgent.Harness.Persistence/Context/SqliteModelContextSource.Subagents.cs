using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Persistence.Context;

public sealed partial class SqliteModelContextSource
{
    public async Task<ModelContextSourceSnapshot> LoadForSubagentContinuationAsync(RunId runId, CancellationToken ct)
    {
        var snapshot = await LoadAsync(runId, ct).ConfigureAwait(false);
        ValidateClosedSubagentHistory(snapshot.SessionBase.Concat(snapshot.RunLocal)
            .Where(entry => entry.SourceId != SessionWorkState.ContextSourceId).ToArray());
        return snapshot;
    }

    private async Task<IReadOnlyList<string>> InheritSubagentContextAsync(SqliteConnection connection, SqliteTransaction transaction, RunId runId,
        List<ModelContextEntry> sessionBase, int depth, CancellationToken ct)
    {
        if (depth > 2) throw new HarnessStoreException("subagent_context_depth_invalid");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$run", runId.Value);
        command.CommandText = """
            SELECT own.attempt_number,previous.run_id FROM agent_subagent_attempts own
            LEFT JOIN agent_subagent_attempts previous ON previous.agent_id=own.agent_id
                AND previous.attempt_number=own.attempt_number-1 WHERE own.run_id=$run
            """;
        RunId previousId;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.GetInt32(0) == 1) return [];
            if (reader.IsDBNull(1)) throw new HarnessStoreException("subagent_context_predecessor_missing");
            previousId = new(reader.GetString(1));
        }
        var previous = await HarnessSqliteQueries.GetRunAsync(connection, transaction, previousId, ct).ConfigureAwait(false);
        var current = await HarnessSqliteQueries.GetRunAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        if (previous is null || current is null || previous.State is not (HarnessRunState.Completed or HarnessRunState.Failed)
            || current.SessionId != previous.SessionId || current.WorkspaceId != previous.WorkspaceId
            || current.PolicySnapshotSha256 != previous.PolicySnapshotSha256 || current.Preset != previous.Preset
            || current.PermissionProfileVersion != previous.PermissionProfileVersion)
            throw new HarnessStoreException("subagent_context_predecessor_invalid");
        var source = await LoadAsync(previousId, depth + 1, ct).ConfigureAwait(false);
        // Work state is session-wide and is appended freshly below. Inheriting
        // its old snapshot as well would duplicate the reserved context identity.
        var inherited = source.SessionBase.Concat(source.RunLocal)
            .Where(entry => entry.SourceId != SessionWorkState.ContextSourceId).ToArray();
        ValidateClosedSubagentHistory(inherited);
        if (sessionBase.Count != 0) throw new HarnessStoreException("subagent_context_base_conflict");
        for (var index = 0; index < inherited.Length; index++)
        {
            var entry = inherited[index];
            long sequence = index + 1;
            sessionBase.Add(entry.Kind switch
            {
                ModelContextEntryKind.Text => ModelContextEntry.Text(entry.SourceId, sequence, entry.Role, entry.Content!),
                ModelContextEntryKind.AssistantStep => ModelContextEntry.Assistant(entry.SourceId, sequence, entry.AssistantStep!),
                ModelContextEntryKind.ToolResult => ModelContextEntry.ToolResult(entry.SourceId, sequence, entry.ToolReceipt!,
                    entry.Content!, entry.ExecutionStartedAtUtc),
                ModelContextEntryKind.Summary => ModelContextEntry.Summary(entry.SourceId, sequence, entry.Content!, entry.SummaryRanges!),
                _ => throw new HarnessStoreException("subagent_context_entry_invalid"),
            });
        }
        return source.ObservedFilePaths;
    }

    private static void ValidateClosedSubagentHistory(IReadOnlyList<ModelContextEntry> entries)
    {
        var pending = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.Kind == ModelContextEntryKind.ToolResult)
            {
                if (!pending.Remove(entry.ToolReceipt!.ProviderCallId!))
                    throw new HarnessStoreException("subagent_continuation_history_incomplete");
                continue;
            }
            if (pending.Count != 0) throw new HarnessStoreException("subagent_continuation_history_incomplete");
            if (entry.Kind == ModelContextEntryKind.AssistantStep)
                foreach (var call in entry.AssistantStep!.ToolCalls) pending.Add(call.ProviderCallId);
        }
        if (pending.Count != 0) throw new HarnessStoreException("subagent_continuation_history_incomplete");
    }
}
