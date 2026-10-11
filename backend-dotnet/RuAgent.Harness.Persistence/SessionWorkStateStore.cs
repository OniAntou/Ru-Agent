using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Persistence.Privacy;

namespace RuAgent.Harness.Persistence;

public sealed record SessionWorkStateSnapshot(
    SessionWorkState State,
    DateTimeOffset UpdatedAt,
    bool HasUnrepresentedReceipts);

public sealed class SessionWorkStateStore(
    HarnessSqliteOptions options,
    SessionWorkStateCryptography cryptography)
{
    private readonly HarnessSqliteOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly SessionWorkStateCryptography _cryptography = cryptography ?? throw new ArgumentNullException(nameof(cryptography));

    public async Task<SessionWorkState?> GetAsync(
        SessionId sessionId,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSnapshotAsync(sessionId, workspaceId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return null;
        return snapshot.HasUnrepresentedReceipts
            ? snapshot.State.WithUnrepresentedReceipts()
            : snapshot.State;
    }

    public async Task<SessionWorkStateSnapshot?> GetSnapshotAsync(
        SessionId sessionId,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        _ = sessionId.Value;
        _ = workspaceId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT workspace_identity_sha256, source_run_id, source_event_id, version, protection_version,
                   nonce, ciphertext, authentication_tag, updated_at_utc
            FROM agent_session_work_states
            WHERE session_id = $session_id AND workspace_id = $workspace_id
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.Value);
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var workspaceHash = reader.GetString(0);
        var sourceRunId = new RunId(reader.GetString(1));
        var sourceEventId = new EventId(reader.GetString(2));
        var version = reader.GetInt64(3);
        var envelope = new EncryptedHarnessPayload(
            reader.GetInt32(4), reader.GetFieldValue<byte[]>(5),
            reader.GetFieldValue<byte[]>(6), reader.GetFieldValue<byte[]>(7));
        var updatedAt = HarnessSqliteMapper.ParseUtc(reader.GetString(8));
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            throw new HarnessStoreException("session_work_state_duplicate");
        await reader.DisposeAsync().ConfigureAwait(false);
        var currentHash = await ReadWorkspaceIdentityHashAsync(connection, null, workspaceId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(workspaceHash, currentHash, StringComparison.Ordinal))
            throw new HarnessStoreException("session_work_state_workspace_changed");
        var state = await _cryptography.UnprotectAsync(sessionId, workspaceId, workspaceHash, sourceRunId,
            version, sourceEventId, envelope, cancellationToken).ConfigureAwait(false);
        var hasNewerReceipts = await HasReceiptsAfterSourceAsync(connection, null, sessionId, workspaceId,
            sourceRunId, sourceEventId, null, null, null, cancellationToken).ConfigureAwait(false);
        return new SessionWorkStateSnapshot(state, updatedAt,
            state.HasUnrepresentedReceipts || hasNewerReceipts);
    }

    internal async Task<SessionWorkState?> GetInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken,
        RunId? excludedReceiptRunId = null,
        long? excludedReceiptSequenceStart = null,
        long? excludedReceiptSequenceEnd = null)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT workspace_identity_sha256, source_run_id, source_event_id, version, protection_version,
                   nonce, ciphertext, authentication_tag
            FROM agent_session_work_states
            WHERE session_id = $session_id AND workspace_id = $workspace_id
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.Value);
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        var workspaceHash = reader.GetString(0);
        var sourceRunId = new RunId(reader.GetString(1));
        var sourceEventId = new EventId(reader.GetString(2));
        var version = reader.GetInt64(3);
        var envelope = new EncryptedHarnessPayload(
            reader.GetInt32(4), reader.GetFieldValue<byte[]>(5),
            reader.GetFieldValue<byte[]>(6), reader.GetFieldValue<byte[]>(7));
        await reader.DisposeAsync().ConfigureAwait(false);
        var currentHash = await ReadWorkspaceIdentityHashAsync(connection, transaction, workspaceId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(workspaceHash, currentHash, StringComparison.Ordinal))
            throw new HarnessStoreException("session_work_state_workspace_changed");
        var state = await _cryptography.UnprotectAsync(sessionId, workspaceId, workspaceHash, sourceRunId,
            version, sourceEventId, envelope, cancellationToken).ConfigureAwait(false);
        var hasNewerReceipts = await HasReceiptsAfterSourceAsync(connection, transaction, sessionId,
            workspaceId, sourceRunId, sourceEventId, excludedReceiptRunId,
            excludedReceiptSequenceStart, excludedReceiptSequenceEnd, cancellationToken).ConfigureAwait(false);
        return hasNewerReceipts ? state.WithUnrepresentedReceipts() : state;
    }

    internal async Task<bool> SaveAsync(
        SessionWorkState state,
        long expectedVersion,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (expectedVersion < 0 || state.Version != expectedVersion + 1)
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        var health = await HarnessSqliteSchema.EnsureInitializedAsync(_options, cancellationToken).ConfigureAwait(false);
        if (!health.IsAvailable) throw new HarnessStoreException(health.ErrorCode ?? "harness_store_unavailable");
        await using var connection = await _options.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var written = await WriteInTransactionAsync(connection, transaction, state,
            expectedVersion, updatedAt, cancellationToken).ConfigureAwait(false);
        if (written) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        else await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return written;
    }

    internal async Task<bool> WriteInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionWorkState state,
        long expectedVersion,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var currentHash = await ReadWorkspaceIdentityHashAsync(connection, transaction, state.WorkspaceId, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(currentHash, state.WorkspaceIdentitySha256, StringComparison.Ordinal))
            throw new HarnessStoreException("session_work_state_workspace_changed");
        await ValidateFactSourcesAsync(connection, transaction, state, cancellationToken).ConfigureAwait(false);
        await ValidateSourceKindsAsync(connection, transaction, state, cancellationToken).ConfigureAwait(false);
        var encrypted = await _cryptography.ProtectAsync(state, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_session_work_states(
                session_id, workspace_id, workspace_identity_sha256, source_run_id, source_event_id, version,
                protection_version, nonce, ciphertext, authentication_tag, updated_at_utc)
            VALUES ($session_id, $workspace_id, $workspace_hash, $source_run_id, $source_event_id, $version,
                $protection_version, $nonce, $ciphertext, $authentication_tag, $updated_at_utc)
            ON CONFLICT(session_id, workspace_id) DO UPDATE SET
                workspace_id = excluded.workspace_id,
                workspace_identity_sha256 = excluded.workspace_identity_sha256,
                source_run_id = excluded.source_run_id,
                source_event_id = excluded.source_event_id,
                version = excluded.version,
                protection_version = excluded.protection_version,
                nonce = excluded.nonce,
                ciphertext = excluded.ciphertext,
                authentication_tag = excluded.authentication_tag,
                updated_at_utc = excluded.updated_at_utc
            WHERE agent_session_work_states.version = $expected_version
            """;
        command.Parameters.AddWithValue("$session_id", state.SessionId.Value);
        command.Parameters.AddWithValue("$workspace_id", state.WorkspaceId.Value);
        command.Parameters.AddWithValue("$workspace_hash", state.WorkspaceIdentitySha256);
        command.Parameters.AddWithValue("$source_run_id", state.SourceRunId.Value);
        command.Parameters.AddWithValue("$source_event_id", state.SourceEventId);
        command.Parameters.AddWithValue("$version", state.Version);
        command.Parameters.AddWithValue("$protection_version", encrypted.ProtectionVersion);
        command.Parameters.AddWithValue("$nonce", encrypted.Nonce.ToArray());
        command.Parameters.AddWithValue("$ciphertext", encrypted.Ciphertext.ToArray());
        command.Parameters.AddWithValue("$authentication_tag", encrypted.AuthenticationTag.ToArray());
        command.Parameters.AddWithValue("$updated_at_utc", HarnessSqliteMapper.Utc(updatedAt));
        command.Parameters.AddWithValue("$expected_version", expectedVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    private static async Task ValidateFactSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionWorkState state,
        CancellationToken cancellationToken)
    {
        var sources = EnumerateSources(state).Distinct().ToArray();
        foreach (var source in sources)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT 1 FROM agent_events e
                JOIN agent_runs r ON r.run_id = e.run_id
                WHERE e.event_id = $event_id AND e.run_id = $run_id
                  AND r.session_id = $session_id AND r.workspace_id = $workspace_id
                LIMIT 1
                """;
            command.Parameters.AddWithValue("$event_id", source.EventId);
            command.Parameters.AddWithValue("$run_id", source.RunId);
            command.Parameters.AddWithValue("$session_id", state.SessionId.Value);
            command.Parameters.AddWithValue("$workspace_id", state.WorkspaceId.Value);
            if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
                throw new HarnessStoreException("session_work_state_source_mismatch");
        }
    }

    private static async Task<bool> HasReceiptsAfterSourceAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SessionId sessionId,
        WorkspaceId workspaceId,
        RunId sourceRunId,
        EventId sourceEventId,
        RunId? excludedReceiptRunId,
        long? excludedReceiptSequenceStart,
        long? excludedReceiptSequenceEnd,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT CASE WHEN EXISTS(
                SELECT 1 FROM agent_events AS source
                JOIN agent_runs AS source_run ON source_run.run_id = source.run_id
                WHERE source.run_id = $source_run_id AND source.event_id = $source_event_id
                  AND source_run.session_id = $session_id AND source_run.workspace_id = $workspace_id
            ) THEN EXISTS(
                SELECT 1
                FROM agent_events AS source
                JOIN agent_runs AS receipt_run
                  ON receipt_run.session_id = $session_id AND receipt_run.workspace_id = $workspace_id
                JOIN agent_events AS receipt INDEXED BY ix_agent_events_session_receipts
                  ON receipt.run_id = receipt_run.run_id
                WHERE source.run_id = $source_run_id AND source.event_id = $source_event_id
                  AND receipt.kind = 'tool.execution_completed'
                  AND receipt.rowid > source.rowid
                  AND ($exclude_run_id IS NULL OR receipt.run_id <> $exclude_run_id
                    OR receipt.sequence < $exclude_sequence_start
                    OR receipt.sequence > $exclude_sequence_end)
            ) ELSE -1 END
            """;
        command.Parameters.AddWithValue("$source_run_id", sourceRunId.Value);
        command.Parameters.AddWithValue("$source_event_id", sourceEventId.Value);
        command.Parameters.AddWithValue("$session_id", sessionId.Value);
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        command.Parameters.AddWithValue("$exclude_run_id", (object?)excludedReceiptRunId?.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("$exclude_sequence_start", (object?)excludedReceiptSequenceStart ?? DBNull.Value);
        command.Parameters.AddWithValue("$exclude_sequence_end", (object?)excludedReceiptSequenceEnd ?? DBNull.Value);
        var result = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        if (result < 0) throw new HarnessStoreException("session_work_state_source_missing");
        return result == 1;
    }

    private static IEnumerable<(string RunId, string EventId)> EnumerateSources(SessionWorkState state)
    {
        yield return (state.SourceRunId.Value, state.SourceEventId);
        yield return (state.UserGoal.SourceRunId, state.UserGoal.SourceEventId);
        foreach (var fact in state.UserConstraints.Concat(state.EarlierUserIntent))
            yield return (fact.SourceRunId, fact.SourceEventId);
        foreach (var item in state.Tasks) yield return (item.SourceRunId, item.SourceEventId);
        foreach (var file in state.ObservedFiles) yield return (file.SourceRunId, file.SourceEventId);
        foreach (var verification in state.Verifications)
            yield return (verification.SourceRunId, verification.SourceEventId);
        if (state.Blocker is not null) yield return (state.Blocker.SourceRunId, state.Blocker.SourceEventId);
        if (state.NextAction is not null) yield return (state.NextAction.SourceRunId, state.NextAction.SourceEventId);
    }

    private static async Task ValidateSourceKindsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionWorkState state,
        CancellationToken cancellationToken)
    {
        foreach (var fact in new[] { state.UserGoal }.Concat(state.UserConstraints).Concat(state.EarlierUserIntent))
            await RequireSourceKindAsync(connection, transaction, state, fact.SourceRunId, fact.SourceEventId,
                "e.kind = 'run.state_changed' AND p.kind = 0", cancellationToken).ConfigureAwait(false);
        foreach (var item in state.Tasks)
        {
            var predicate = item.Source == SessionWorkItemSource.User
                ? "e.kind = 'run.state_changed' AND p.kind = 0"
                : "e.kind = 'tool.execution_completed' AND e.tool_outcome = 0";
            await RequireSourceKindAsync(connection, transaction, state, item.SourceRunId,
                item.SourceEventId, predicate, cancellationToken).ConfigureAwait(false);
        }
        foreach (var source in state.ObservedFiles.Select(item => (item.SourceRunId, item.SourceEventId))
                     .Concat(state.Verifications.Select(item => (item.SourceRunId, item.SourceEventId))))
            await RequireSourceKindAsync(connection, transaction, state, source.SourceRunId,
                source.SourceEventId, "e.kind = 'tool.execution_completed' AND e.tool_outcome = 0",
                cancellationToken).ConfigureAwait(false);
    }

    private static async Task RequireSourceKindAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionWorkState state,
        string runId,
        string eventId,
        string predicate,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT 1 FROM agent_events e
            JOIN agent_runs r ON r.run_id = e.run_id
            LEFT JOIN agent_payloads p ON p.payload_id = e.payload_id
            WHERE e.event_id = $event_id AND e.run_id = $run_id
              AND r.session_id = $session_id AND r.workspace_id = $workspace_id
              AND {predicate}
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$event_id", eventId);
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$session_id", state.SessionId.Value);
        command.Parameters.AddWithValue("$workspace_id", state.WorkspaceId.Value);
        if (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is null)
            throw new HarnessStoreException("session_work_state_source_kind_mismatch");
    }

    internal static async Task<string> ReadWorkspaceIdentityHashAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        WorkspaceId workspaceId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT display_identity_hash FROM agent_workspaces WHERE workspace_id = $workspace_id";
        command.Parameters.AddWithValue("$workspace_id", workspaceId.Value);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string
            ?? throw new HarnessStoreException("workspace_not_found");
    }
}
