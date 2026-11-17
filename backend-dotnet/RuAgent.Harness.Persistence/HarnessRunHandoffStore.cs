using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Persistence.Context;

namespace RuAgent.Harness.Persistence;

public sealed partial class HarnessPayloadStore
{
    private const int HandoffEntryLimit = 48;
    private const int MaximumHandoffEntryLimit = 384;
    private const int HandoffPayloadByteLimit = 128 * 1024;

    private sealed class HandoffReadBudget(int entryLimit)
    {
        public int EntryLimit { get; } = entryLimit;
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
        var context = await ReadSessionRunHandoffContextAsync(targetId, sessionId, visibleSourceRunIds, ct)
            .ConfigureAwait(false);
        return context?.RenderedHandoff;
    }

    /// <param name="maximumCharacters">The rendered size; entry reads and excerpt clips scale with it.</param>
    /// <param name="nativeSourceRunId">
    /// A source run the target continues natively (ADR 0049). It still supplies scope hints,
    /// but is not rendered: the target reads its history as native entries instead.
    /// </param>
    public async Task<SessionRunHandoffContext?> ReadSessionRunHandoffContextAsync(RunId targetId, SessionId sessionId,
        IReadOnlyCollection<string> visibleSourceRunIds, CancellationToken ct,
        int maximumCharacters = SessionRunHandoff.DefaultCharacters, RunId? nativeSourceRunId = null)
    {
        var visible = ValidateVisibleSources(visibleSourceRunIds);
        var options = RequireOptions();
        await using var connection = await options.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var target = await ReadHandoffTargetAsync(connection, transaction, targetId, sessionId, ct).ConfigureAwait(false);
        if (target is null || visible.Length == 0) return null;
        var runs = await ReadHandoffSourcesAsync(connection, transaction, target, visible, SessionRunHandoff.MaximumRuns, ct)
            .ConfigureAwait(false);
        if (runs.Count == 0) return null;

        var contextSource = new SqliteModelContextSource(options, this);
        var budget = new HandoffReadBudget((int)Math.Min(MaximumHandoffEntryLimit,
            HandoffEntryLimit * SessionRunHandoff.Scale(maximumCharacters)));
        var histories = new List<RunHandoffHistory>();
        var observedPaths = new List<string>(SessionScopeHints.MaximumPaths);
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Spend the read budget on the newest run first; display chronologically.
        foreach (var run in runs)
        {
            budget.Omitted = false;
            budget.OmittedPayloadCount = 0;
            var summaryRead = await ReadHandoffSummariesAsync(connection, transaction, run.RunId, budget, ct)
                .ConfigureAwait(false);
            var entries = await ReadHandoffEntriesAsync(connection, transaction, contextSource, run.RunId, budget, ct)
                .ConfigureAwait(false);
            AddRecentScopeHints(FileObservationPaths.Read(entries), observedPaths, knownPaths);
            if (observedPaths.Count < SessionScopeHints.MaximumPaths)
            {
                var scopeHistory = await ReadHandoffScopeHistoryAsync(connection, transaction, contextSource,
                    run.RunId, entries, budget, ct).ConfigureAwait(false);
                AddRecentScopeHints(FileObservationPaths.Read(entries.Concat(scopeHistory)
                    .OrderBy(entry => entry.Sequence)), observedPaths, knownPaths);
            }
            if (run.RunId == nativeSourceRunId) continue;
            histories.Add(new(run.RunId, run.State, entries, budget.Omitted || summaryRead.Omitted,
                run.TerminalErrorCode, budget.OmittedPayloadCount, summaryRead.Entries));
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        histories.Reverse();
        return new SessionRunHandoffContext(SessionRunHandoff.Render(histories, maximumCharacters),
            Array.AsReadOnly(observedPaths.AsEnumerable().Reverse().ToArray()));
    }

    private static string[] ValidateVisibleSources(IReadOnlyCollection<string> visibleSourceRunIds)
    {
        ArgumentNullException.ThrowIfNull(visibleSourceRunIds);
        var visible = visibleSourceRunIds.Distinct(StringComparer.Ordinal).ToArray();
        if (visible.Length > 256 || visible.Any(id => string.IsNullOrEmpty(id) || id.Length > 128))
            throw new HarnessStoreException("handoff_sources_invalid");
        return visible;
    }

    /// <summary>
    /// The target run when it is a live root run whose base is not captured yet; null when
    /// it may not receive history from earlier runs.
    /// </summary>
    private static async Task<AgentRun?> ReadHandoffTargetAsync(SqliteConnection connection,
        SqliteTransaction transaction, RunId targetId, SessionId sessionId, CancellationToken ct)
    {
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
        return target;
    }

    /// <summary>Visible terminal root runs before the target in its session and workspace, newest first.</summary>
    private static async Task<List<AgentRun>> ReadHandoffSourcesAsync(SqliteConnection connection,
        SqliteTransaction transaction, AgentRun target, string[] visible, int limit, CancellationToken ct)
    {
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
        sources.Parameters.AddWithValue("$run", target.RunId.Value);
        sources.Parameters.AddWithValue("$session", target.SessionId.Value);
        sources.Parameters.AddWithValue("$workspace", target.WorkspaceId.Value);
        sources.Parameters.AddWithValue("$limit", limit);
        var runs = new List<AgentRun>();
        using var reader = await sources.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) runs.Add(HarnessSqliteMapper.ReadRun(reader));
        return runs;
    }

    private static void AddRecentScopeHints(IReadOnlyList<string> paths, List<string> retained,
        HashSet<string> knownPaths)
    {
        for (var index = paths.Count - 1; index >= 0 && retained.Count < SessionScopeHints.MaximumPaths; index--)
            if (knownPaths.Add(paths[index])) retained.Add(paths[index]);
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
                AND substr(p.context_source_id,1,6)='event:'
                AND e.event_id=substr(p.context_source_id,7)
            WHERE p.run_id=$run AND p.context_scope=1 AND (p.audience & 2)=2
              AND p.kind IN (0,1,3,7,13)
            """;
        command.CommandText = selectEntriesSql + " ORDER BY p.context_sequence DESC LIMIT $limit";
        command.Parameters.AddWithValue("$run", runId.Value);
        command.Parameters.AddWithValue("$limit", budget.EntryLimit + 1);
        var entries = new List<ModelContextEntry>();
        bool hasPrompt = false;
        using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (entries.Count == budget.EntryLimit) { budget.Omitted = true; break; }
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
