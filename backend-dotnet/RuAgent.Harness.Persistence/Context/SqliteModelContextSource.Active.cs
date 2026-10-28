using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Persistence.Context;

public sealed partial class SqliteModelContextSource
{
    private async Task<ModelContextSourceSnapshot> LoadProjectedAsync(RunId runId, int depth, CancellationToken ct)
    {
        await using var connection = await _options.OpenConnectionAsync(ct).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: true);
        var header = await ReadContextHeaderAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        string key;
        try { key = await _payloadStore.ReadModelCacheKeyStampAsync(ct).ConfigureAwait(false); }
        catch { _activeCache?.Clear(); _entryCache?.Clear(); throw; }
        var cached = _activeCache?.Get(runId.Value, key, header.Revision, header.Version, header.Sequence, header.BaseCount);
        if (_activeCache is not null) Core.Diagnostics.HarnessPerformance.RecordContextProjection(cached is not null);
        var sessionBase = cached?.Base.ToList() ?? [];
        var runLocal = cached?.Local.ToList() ?? [];
        var appended = new List<ModelContextEntry>();
        if (cached is null || cached.Version != header.Version)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = ContextEntriesSql.Replace("AND p.context_scope IS NOT NULL", """
                AND p.context_scope IS NOT NULL
                AND ($cold=1 OR (p.context_scope=1 AND p.context_sequence>$after))
                AND (p.context_scope=0 OR p.context_sequence<=$through)
                """, StringComparison.Ordinal);
            command.Parameters.AddWithValue("$run_id", runId.Value);
            command.Parameters.AddWithValue("$cold", cached is null ? 1 : 0);
            command.Parameters.AddWithValue("$after", cached?.Sequence ?? 0);
            command.Parameters.AddWithValue("$through", header.Sequence);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var entry = await ReadEntryAsync(runId, reader, ct, key).ConfigureAwait(false);
                if (reader.GetInt32(9) == 0) sessionBase.Add(entry);
                else { runLocal.Add(entry); appended.Add(entry); }
            }
        }
        if (cached is null && sessionBase.Count != header.BaseCount) throw new HarnessStoreException("context_base_incomplete");
        // Seed only the call association, never replay old receipts against seeded checks.
        var evidenceEntries = cached?.LastAssistant is { } assistant
            ? appended.Prepend(assistant).ToArray() : appended.ToArray();
        var capturedPaths = sessionBase.SelectMany(SessionScopeHints.Decode).ToArray();
        var paths = FileObservationPaths.Read(evidenceEntries, cached?.Paths ?? capturedPaths);
        var verification = RunVerificationEvidence.Project(evidenceEntries, cached?.Verification);
        var checkStarts = verification.Checks
            .Where(check => check.ExecutionStartedAtUtc is not null)
            .Select(check => check.ExecutionStartedAtUtc!.Value.ToUniversalTime())
            .Distinct()
            .ToArray();
        var workspaceFreshness = await ReadWorkspaceJournalFreshnessAsync(
                connection, transaction, runId, checkStarts, ct)
            .ConfigureAwait(false);
        verification = verification.ApplyWorkspaceFreshness(workspaceFreshness);
        var lastAssistant = appended.LastOrDefault(e => e.AssistantStep is not null) ?? cached?.LastAssistant;
        IReadOnlyList<ModelContextEntry> skillBaseEntries = cached is null ? sessionBase : Array.Empty<ModelContextEntry>();
        IReadOnlyList<ModelContextEntry> skillRunEntries = cached is null ? runLocal : evidenceEntries;
        var activeSkills = ActiveSkillSnapshots.Project(skillBaseEntries, skillRunEntries, cached?.ActiveSkills,
            cached?.SkillOmissions, cached?.AdditionalSkillOmissionCount ?? 0);
        // Keep source IDs for compaction ranges, but remove the encoded seed from
        // compaction input so it cannot be copied into a summary.
        ReplaceSkillInvocationSeeds(sessionBase);
        ReplaceSkillInvocationSeeds(runLocal);
        await ApplyCompactionsAsync(connection, transaction, runId, sessionBase, runLocal,
            cached?.Version ?? -1, header.Version, ct).ConfigureAwait(false);

        // Cache only active model entries and bounded receipt projections. Images,
        // dynamic work state and inherited child context are refreshed separately.
        var weight = ActiveContextCache.Weight(sessionBase.Concat(runLocal))
            + (lastAssistant is null ? 0 : ActiveContextCache.Weight([lastAssistant]))
            + ActiveContextCache.SkillWeight(activeSkills.Snapshots)
            + ActiveContextCache.SkillOmissionWeight(activeSkills.Omissions)
            + 64L + 256 * 1024;
        _activeCache?.Store(runId.Value, new ActiveContextCache.Entry(key, header.Revision, header.Version,
            header.Sequence, header.BaseCount, sessionBase.ToArray(), runLocal.ToArray(), paths,
            verification, lastAssistant, activeSkills.Snapshots, activeSkills.Omissions,
            activeSkills.AdditionalOmissionCount, weight, DateTimeOffset.UtcNow));
        var workflow = ActiveSkillSnapshots.CreateWorkflowEntry(activeSkills.Snapshots, sessionBase.Concat(runLocal),
            activeSkills.OmissionEntry is null ? 0 : 1);
        if (workflow is not null) runLocal.Add(workflow);
        if (activeSkills.OmissionEntry is not null) runLocal.Add(activeSkills.OmissionEntry);
        await AttachToolImagesAsync(connection, transaction, runId, runLocal, ct).ConfigureAwait(false);
        var inheritedPaths = await InheritSubagentContextAsync(connection, transaction, runId, sessionBase, depth, ct).ConfigureAwait(false);
        await AppendSessionWorkStateAsync(connection, transaction, runId, sessionBase, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new ModelContextSourceSnapshot(sessionBase.Where(e => e.SourceId != SessionScopeHints.SourceId
                && !ActiveSkillSnapshots.IsReservedSourceId(e.SourceId)).ToArray(),
            runLocal.Where(e => !ActiveSkillSnapshots.IsReservedSourceId(e.SourceId)).ToArray(),
            inheritedPaths.Concat(paths).ToArray(), verification);
    }

    private sealed record ContextHeader(int BaseCount, long Revision, long Version, long Sequence);

    private sealed record WorkspaceJournalState(
        long CommitSequence,
        DateTimeOffset? UpdatedAtUtc,
        long? LatestRetainedSequence,
        DateTimeOffset? LatestRetainedAtUtc,
        bool CommitSequenceKnown);

    private static async Task<IReadOnlyDictionary<DateTimeOffset, WorkspaceCheckFreshness>> ReadWorkspaceJournalFreshnessAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        IReadOnlyList<DateTimeOffset> checkStarts,
        CancellationToken ct)
    {
        if (checkStarts.Count == 0) return new Dictionary<DateTimeOffset, WorkspaceCheckFreshness>();

        var state = await ReadWorkspaceJournalStateAsync(connection, transaction, runId, ct).ConfigureAwait(false);
        if (state is null) return checkStarts.ToDictionary(start => start, _ => default(WorkspaceCheckFreshness));
        return checkStarts.ToDictionary(start => start, start =>
        {
            if (!state.CommitSequenceKnown) return default;
            if (state.CommitSequence == 0)
                return state.LatestRetainedSequence is null
                    ? new WorkspaceCheckFreshness(IsKnown: true, ChangedAfterStart: false)
                    : default;
            if (state.LatestRetainedSequence is { } latestSequence && latestSequence > state.CommitSequence)
                return default;

            // The coordinator timestamp is an upper bound for all journal writes,
            // but policy updates also advance it. A retained row at the current
            // high-water sequence gives the precise latest journal mutation time.
            if (state.LatestRetainedSequence == state.CommitSequence)
            {
                if (state.LatestRetainedAtUtc is { } latestAt)
                    return new WorkspaceCheckFreshness(IsKnown: true, ChangedAfterStart: latestAt >= start);
                if (state.UpdatedAtUtc is { } latestCoordinatorUpdate && start <= latestCoordinatorUpdate)
                    return new WorkspaceCheckFreshness(IsKnown: true, ChangedAfterStart: true);
            }

            if (state.UpdatedAtUtc is { } updated && start > updated)
                return new WorkspaceCheckFreshness(IsKnown: true, ChangedAfterStart: false);
            return default;
        });
    }

    private static async Task<WorkspaceJournalState?> ReadWorkspaceJournalStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RunId runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT run.workspace_id,
                   coordinator.commit_sequence,
                   coordinator.updated_at_utc,
                   (SELECT MAX(journal.commit_sequence) FROM workspace_change_journal journal
                    WHERE journal.workspace_id = run.workspace_id),
                   (SELECT journal.created_at_utc FROM workspace_change_journal journal
                    WHERE journal.workspace_id = run.workspace_id
                    ORDER BY journal.commit_sequence DESC LIMIT 1)
            FROM agent_runs run
            LEFT JOIN workspace_coordinators coordinator ON coordinator.workspace_id = run.workspace_id
            WHERE run.run_id = $run_id
            """;
        command.Parameters.AddWithValue("$run_id", runId.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false) || reader.IsDBNull(1))
            return null;

        var commitSequence = reader.GetInt64(1);
        DateTimeOffset? updatedAtUtc = null;
        bool commitSequenceKnown = commitSequence >= 0;
        if (!reader.IsDBNull(2)
            && DateTimeOffset.TryParse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var updated))
            updatedAtUtc = updated.ToUniversalTime();
        long? latestRetainedSequence = reader.IsDBNull(3) ? null : reader.GetInt64(3);
        DateTimeOffset? latestRetainedAtUtc = null;
        if (!reader.IsDBNull(4)
            && DateTimeOffset.TryParse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var latest))
            latestRetainedAtUtc = latest.ToUniversalTime();

        return new WorkspaceJournalState(commitSequence, updatedAtUtc, latestRetainedSequence,
            latestRetainedAtUtc, commitSequenceKnown);
    }

    private static void ReplaceSkillInvocationSeeds(List<ModelContextEntry> entries)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (!ActiveSkillSnapshots.IsReservedSourceId(entry.SourceId)) continue;
            entries[index] = ModelContextEntry.Text(entry.SourceId, entry.Sequence, entry.Role,
                "[skill workflow receipt]");
        }
    }

    private static async Task<ContextHeader> ReadContextHeaderAsync(SqliteConnection connection,
        SqliteTransaction transaction, RunId run, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT base_context_captured_at_utc,base_context_entry_count,context_revision,version,last_sequence
            FROM agent_runs WHERE run_id=$run
            """;
        command.Parameters.AddWithValue("$run", run.Value);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new HarnessStoreException("context_run_not_found");
        if (reader.IsDBNull(0)) throw new HarnessStoreException("context_base_missing");
        return new(reader.GetInt32(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4));
    }
}
