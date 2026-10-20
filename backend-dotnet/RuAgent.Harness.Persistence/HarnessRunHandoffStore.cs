using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessPayloadStore
{
    private const int HandoffEntryLimit = 48;
    private const int HandoffPayloadByteLimit = 128 * 1024;

    private sealed class HandoffReadBudget
    {
        public int RemainingBytes = 2 * 1024 * 1024;
        public bool Omitted;
        public int OmittedPayloadCount;
    }

    /// <summary>
    /// Reads bounded model-visible evidence from earlier terminal root runs.
    /// One read snapshot binds scope, source selection, payloads and receipts.
    /// The caller persists the rendered handoff with its immutable base context.
    /// </summary>
    public async Task<string?> ReadSessionRunHandoffAsync(RunId targetId, SessionId sessionId,
        IReadOnlyCollection<string> visibleSourceRunIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(visibleSourceRunIds);
        var visible = visibleSourceRunIds.Distinct(StringComparer.Ordinal).ToArray();
        if (visible.Length > 256 || visible.Any(id => string.IsNullOrEmpty(id) || id.Length > 128))
            throw new HarnessStoreException("handoff_sources_invalid");
        var options = RequireOptions();
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var target = await HarnessSqliteQueries.GetRunAsync(connection, transaction, targetId, ct).ConfigureAwait(false)
            ?? throw new HarnessStoreException("handoff_run_not_found");
        if (target.SessionId != sessionId) throw new HarnessStoreException("handoff_scope_mismatch");
        using var guard = connection.CreateCommand();
        guard.Transaction = transaction;
        guard.CommandText = """
            SELECT base_context_captured_at_utc IS NOT NULL
                OR EXISTS(SELECT 1 FROM agent_internal_runs WHERE run_id=$run)
                OR EXISTS(SELECT 1 FROM agent_subagent_attempts WHERE run_id=$run)
                OR EXISTS(SELECT 1 FROM agent_session_tombstones WHERE session_id=$session)
            FROM agent_runs WHERE run_id=$run
            """;
        guard.Parameters.AddWithValue("$run", targetId.Value);
        guard.Parameters.AddWithValue("$session", sessionId.Value);
        if (target.State.IsTerminal() || (long)(await guard.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 1L) != 0)
            return null;
        if (visible.Length == 0) return null;

        using var sources = connection.CreateCommand();
        sources.Transaction = transaction;
        var sourceParameters = visible.Select((_, index) => "$source" + index).ToArray();
        for (int index = 0; index < visible.Length; index++)
            sources.Parameters.AddWithValue(sourceParameters[index], visible[index]);
        // rowid is only a snapshot-local insertion fence, not a stored identity.
        // It excludes later runs even if timestamps repeat or the clock moves back.
        sources.CommandText = $"""
            SELECT {HarnessSqliteMapper.RunColumns} FROM agent_runs r
            WHERE r.session_id=$session AND r.workspace_id=$workspace
              AND r.run_id IN ({string.Join(',', sourceParameters)})
              AND r.rowid < (SELECT rowid FROM agent_runs WHERE run_id=$run)
              AND r.terminal_at_utc IS NOT NULL AND r.state IN (7,8,9,10)
              AND NOT EXISTS(SELECT 1 FROM agent_internal_runs i WHERE i.run_id=r.run_id)
              AND NOT EXISTS(SELECT 1 FROM agent_subagent_attempts a WHERE a.run_id=r.run_id)
            ORDER BY r.created_at_utc DESC, r.run_id DESC LIMIT $limit
            """;
        sources.Parameters.AddWithValue("$run", targetId.Value);
        sources.Parameters.AddWithValue("$session", sessionId.Value);
        sources.Parameters.AddWithValue("$workspace", target.WorkspaceId.Value);
        sources.Parameters.AddWithValue("$limit", SessionRunHandoff.MaximumRuns);
        var runs = new List<AgentRun>();
        using (var reader = await sources.ExecuteReaderAsync(ct).ConfigureAwait(false))
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) runs.Add(HarnessSqliteMapper.ReadRun(reader));
        if (runs.Count == 0) return null;

        var contextSource = new SqliteModelContextSource(options, this);
        var budget = new HandoffReadBudget();
        var histories = new List<RunHandoffHistory>();
        // Spend the read budget on the newest run first; display chronologically.
        foreach (var run in runs)
        {
            budget.Omitted = false;
            budget.OmittedPayloadCount = 0;
            var entries = await ReadHandoffEntriesAsync(connection, transaction, contextSource, run.RunId, budget, ct)
                .ConfigureAwait(false);
            histories.Add(new(run.RunId, run.State, entries, budget.Omitted, run.TerminalErrorCode, budget.OmittedPayloadCount));
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        histories.Reverse();
        return SessionRunHandoff.Render(histories);
    }

    private static async Task<IReadOnlyList<ModelContextEntry>> ReadHandoffEntriesAsync(
        SqliteConnection connection, SqliteTransaction transaction, SqliteModelContextSource source,
        RunId runId, HandoffReadBudget budget, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // Base history is intentionally excluded: it would recursively duplicate
        // handoffs and completed conversation turns. Never inspect audit-only bytes,
        // provider continuation, image data, approval tokens or recovery payloads.
        const string selectEntriesSql = """
            SELECT p.payload_id,p.kind,p.audience,p.encoding,p.byte_cap,
                   p.protected_content,
                   p.protection_version,p.nonce,p.authentication_tag,p.context_scope,
                   p.context_source_id,p.context_sequence,p.context_role,p.context_entry_kind,
                   e.tool_call_id,e.provider_call_id,e.execution_id,e.safe_code,e.tool_outcome,e.side_effects_possible,
                   length(p.protected_content)
            FROM agent_payloads p LEFT JOIN agent_events e ON e.run_id=p.run_id
                AND ('event:' || e.event_id)=p.context_source_id
            WHERE p.run_id=$run AND p.context_scope=1 AND (p.audience & 2)=2
              AND p.kind IN (0,1,3,7,13)
            """;
        command.CommandText = selectEntriesSql + " ORDER BY p.context_sequence DESC LIMIT $limit";
        command.Parameters.AddWithValue("$run", runId.Value);
        command.Parameters.AddWithValue("$limit", HandoffEntryLimit + 1);
        var entries = new List<ModelContextEntry>();
        bool hasPrompt = false;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (entries.Count == HandoffEntryLimit) { budget.Omitted = true; break; }
                hasPrompt |= reader.GetInt32(1) == (int)HarnessPayloadKind.UserPrompt;
                entries.Add(await ReadHandoffEntryAsync(reader, source, runId, budget, ct).ConfigureAwait(false));
            }
        }
        if (!hasPrompt)
        {
            command.CommandText = selectEntriesSql + " AND p.kind=0 ORDER BY p.context_sequence ASC LIMIT 1";
            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var prompt = await ReadHandoffEntryAsync(reader, source, runId, budget, ct).ConfigureAwait(false);
                if (!entries.Any(entry => entry.SourceId == prompt.SourceId)) entries.Add(prompt);
            }
        }
        return entries.OrderBy(entry => entry.Sequence).ToArray();
    }

    private static async Task<ModelContextEntry> ReadHandoffEntryAsync(SqliteDataReader reader,
        SqliteModelContextSource source, RunId runId, HandoffReadBudget budget, CancellationToken ct)
    {
        int bytes = reader.GetInt32(20);
        // Check length before GetBytes/decryption. Keep the blob a direct column:
        // Microsoft.Data.Sqlite's incremental blob reader needs origin metadata.
        if (bytes <= HandoffPayloadByteLimit && bytes <= budget.RemainingBytes)
        {
            budget.RemainingBytes -= bytes;
            return await source.ReadEntryAsync(runId, reader, ct).ConfigureAwait(false);
        }
        budget.Omitted = true;
        budget.OmittedPayloadCount++;
        string receipt = reader.IsDBNull(18) ? "" :
            $" Recorded receipt outcome={(ToolExecutionOutcome)reader.GetInt32(18)} code={reader.GetString(17)} side_effects_possible={reader.GetBoolean(19)} call={reader.GetString(15)} execution={reader.GetString(16)}.";
        return ModelContextEntry.Text(reader.GetString(10), reader.GetInt64(11), ModelContextRole.User,
            $"[Historical payload omitted by handoff read limit; bytes={bytes}; kind={(HarnessPayloadKind)reader.GetInt32(1)}].{receipt}");
    }
}
