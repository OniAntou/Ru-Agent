using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence.Policy;

public sealed record AgentSessionPermissionSelection(
    SessionId SessionId,
    AgentPermissionPreset Preset,
    long Version,
    DateTimeOffset UpdatedAt,
    int PermissionProfileVersion = 1);

public enum SessionPermissionOutcome
{
    CreatedDefault,
    Matched,
    PresetMismatch,
    SelectionRequired,
    UpdatedInteractively,
    VersionConflict,
}

public enum SessionPresetMutationOutcome
{
    Updated,
    VersionConflict,
    CapacityExceeded,
    TokenConflict,
}

public sealed record SessionPresetMutationResult(
    SessionPresetMutationOutcome Outcome,
    AgentSessionPermissionSelection? Selection,
    string? ErrorCode = null,
    string? SnapshotToken = null);

public enum SessionPresetRestoreOutcome
{
    Restored,
    Replayed,
    NotFound,
    Expired,
    SessionMismatch,
    VersionConflict,
    ReplayConflict,
}

public sealed record SessionPresetRestoreResult(
    SessionPresetRestoreOutcome Outcome,
    AgentSessionPermissionSelection? Selection,
    string? ErrorCode = null,
    bool HadPreviousSelection = false);

public readonly record struct SessionPermissionResult(
    SessionPermissionOutcome Outcome,
    AgentSessionPermissionSelection? Selection,
    string? ErrorCode = null,
    AgentSessionPermissionSelection? PreviousSelection = null,
    bool HadPreviousSelection = false);

public sealed class AgentSessionPermissionStore
{
    public const int MaxSessionPresetSnapshots = 1024;
    public static readonly TimeSpan SessionPresetSnapshotLifetime = TimeSpan.FromMinutes(10);
    private readonly HarnessSqliteOptions _options;

    public AgentSessionPermissionStore(HarnessSqliteOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public async Task<SessionPermissionResult> EnsureForRunAsync(
        SessionId sessionId,
        AgentPermissionPreset preset,
        long selectionVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Validate(sessionId, preset, selectionVersion);
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var result = await EnsureForRunInTransactionAsync(
                    connection,
                    transaction,
                    sessionId,
                    preset,
                    selectionVersion,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("harness_permission_store_failed", exception);
        }
    }

    public async Task<SessionPermissionResult> SetInteractivelyAsync(
        SessionId sessionId,
        AgentPermissionPreset preset,
        long expectedVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _ = sessionId.Value;
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var existing = await GetInTransactionAsync(
                    connection,
                    transaction,
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                if (expectedVersion != 0)
                {
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return new SessionPermissionResult(
                        SessionPermissionOutcome.VersionConflict,
                        null,
                        "preset_version_conflict");
                }

                var created = new AgentSessionPermissionSelection(sessionId, preset, 1, now);
                await InsertAsync(connection, transaction, created, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionPermissionResult(
                    SessionPermissionOutcome.UpdatedInteractively,
                    created,
                    PreviousSelection: null,
                    HadPreviousSelection: false);
            }

            if (existing.Version != expectedVersion)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionPermissionResult(
                    SessionPermissionOutcome.VersionConflict,
                    existing,
                    "preset_version_conflict");
            }

            var updated = existing with
            {
                Preset = preset,
                Version = checked(existing.Version + 1),
                UpdatedAt = now,
                PermissionProfileVersion = AgentPermissionPresetMapper.CurrentPermissionProfileVersion,
            };
            await UpdateAsync(connection, transaction, updated, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SessionPermissionResult(
                SessionPermissionOutcome.UpdatedInteractively,
                updated,
                PreviousSelection: existing,
                HadPreviousSelection: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("harness_permission_store_failed", exception);
        }
    }

    public async Task<SessionPresetMutationResult> SetInteractivelyWithSnapshotAsync(
        SessionId sessionId,
        AgentPermissionPreset preset,
        long expectedVersion,
        string snapshotToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _ = sessionId.Value;
        ValidateSnapshotToken(snapshotToken);
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }

        var normalizedNow = now.ToUniversalTime();
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            await DeleteExpiredSnapshotsInTransactionAsync(
                    connection,
                    transaction,
                    normalizedNow,
                    cancellationToken)
                .ConfigureAwait(false);
            if (await SnapshotExistsInTransactionAsync(
                    connection,
                    transaction,
                    snapshotToken,
                    cancellationToken)
                .ConfigureAwait(false))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionPresetMutationResult(
                    SessionPresetMutationOutcome.TokenConflict,
                    null,
                    "preset_snapshot_token_conflict");
            }

            if (await CountSnapshotsInTransactionAsync(
                    connection,
                    transaction,
                    normalizedNow,
                    cancellationToken)
                .ConfigureAwait(false) >= MaxSessionPresetSnapshots)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionPresetMutationResult(
                    SessionPresetMutationOutcome.CapacityExceeded,
                    null,
                    "preset_snapshot_capacity_exceeded");
            }

            var existing = await GetInTransactionAsync(
                    connection,
                    transaction,
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is null && expectedVersion != 0
                || existing is not null && existing.Version != expectedVersion)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionPresetMutationResult(
                    SessionPresetMutationOutcome.VersionConflict,
                    existing,
                    "preset_version_conflict");
            }

            var applied = existing is null
                ? new AgentSessionPermissionSelection(
                    sessionId,
                    preset,
                    1,
                    normalizedNow,
                    AgentPermissionPresetMapper.CurrentPermissionProfileVersion)
                : existing with
                {
                    Preset = preset,
                    Version = checked(existing.Version + 1),
                    UpdatedAt = normalizedNow,
                    PermissionProfileVersion = AgentPermissionPresetMapper.CurrentPermissionProfileVersion,
                };
            await InsertSnapshotAsync(
                    connection,
                    transaction,
                    snapshotToken,
                    sessionId,
                    existing,
                    applied,
                    normalizedNow,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existing is null)
            {
                await InsertAsync(connection, transaction, applied, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await UpdateAsync(connection, transaction, applied, cancellationToken)
                    .ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SessionPresetMutationResult(
                SessionPresetMutationOutcome.Updated,
                applied,
                SnapshotToken: snapshotToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("harness_permission_store_failed", exception);
        }
    }

    public async Task<SessionPresetRestoreResult> RestoreSnapshotAsync(
        SessionId sessionId,
        string snapshotToken,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        _ = sessionId.Value;
        ValidateSnapshotToken(snapshotToken);
        var normalizedNow = now.ToUniversalTime();
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            var snapshot = await GetSnapshotInTransactionAsync(
                    connection,
                    transaction,
                    snapshotToken,
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionPresetRestoreResult(
                    SessionPresetRestoreOutcome.NotFound,
                    null,
                    "preset_snapshot_not_found");
            }

            if (!string.Equals(snapshot.SessionId, sessionId.Value, StringComparison.Ordinal))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionPresetRestoreResult(
                    SessionPresetRestoreOutcome.SessionMismatch,
                    null,
                    "preset_snapshot_mismatch");
            }

            if (snapshot.ExpiresAt <= normalizedNow)
            {
                await DeleteSnapshotAsync(
                        connection,
                        transaction,
                        snapshotToken,
                        cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionPresetRestoreResult(
                    SessionPresetRestoreOutcome.Expired,
                    null,
                    "preset_snapshot_expired");
            }

            var current = await GetInTransactionAsync(
                    connection,
                    transaction,
                    sessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (snapshot.ConsumedAt is not null)
            {
                var replay = SnapshotMatchesPrevious(snapshot, current);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return replay
                    ? new SessionPresetRestoreResult(
                        SessionPresetRestoreOutcome.Replayed,
                        snapshot.PreviousSelection,
                        HadPreviousSelection: snapshot.HadPreviousSelection)
                    : new SessionPresetRestoreResult(
                        SessionPresetRestoreOutcome.ReplayConflict,
                        null,
                        "preset_snapshot_replay_conflict");
            }

            if (!SnapshotMatchesApplied(snapshot, current))
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionPresetRestoreResult(
                    SessionPresetRestoreOutcome.VersionConflict,
                    current,
                    "preset_version_conflict");
            }

            var affected = await RestoreSnapshotSelectionAsync(
                    connection,
                    transaction,
                    snapshot,
                    cancellationToken)
                .ConfigureAwait(false);
            if (affected != 1)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new SessionPresetRestoreResult(
                    SessionPresetRestoreOutcome.VersionConflict,
                    current,
                    "preset_version_conflict");
            }

            if (await MarkSnapshotConsumedAsync(
                    connection,
                    transaction,
                    snapshot,
                    normalizedNow,
                    cancellationToken)
                .ConfigureAwait(false) != 1)
            {
                return new SessionPresetRestoreResult(
                    SessionPresetRestoreOutcome.VersionConflict,
                    current,
                    "preset_version_conflict");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SessionPresetRestoreResult(
                SessionPresetRestoreOutcome.Restored,
                snapshot.PreviousSelection,
                HadPreviousSelection: snapshot.HadPreviousSelection);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or IOException)
        {
            throw new HarnessStoreException("harness_permission_store_failed", exception);
        }
    }

    public async Task<AgentSessionPermissionSelection?> GetAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        _ = sessionId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        return await GetInTransactionAsync(
                connection,
                transaction: null,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteSessionAsync(
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        _ = sessionId.Value;
        await using var connection = await _options.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM agent_session_settings WHERE session_id = $session_id";
        command.Parameters.AddWithValue("$session_id", sessionId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<SessionPermissionResult> EnsureForRunInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionId sessionId,
        AgentPermissionPreset preset,
        long selectionVersion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        Validate(sessionId, preset, selectionVersion);
        var existing = await GetInTransactionAsync(
                connection,
                transaction,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return existing.Preset == preset
                && existing.Version == selectionVersion
                && existing.PermissionProfileVersion == AgentPermissionPresetMapper.CurrentPermissionProfileVersion
                ? new SessionPermissionResult(SessionPermissionOutcome.Matched, existing)
                : new SessionPermissionResult(
                    SessionPermissionOutcome.PresetMismatch,
                    existing,
                    "preset_mismatch");
        }

        if (preset == AgentPermissionPreset.FullAccess)
        {
            return new SessionPermissionResult(
                SessionPermissionOutcome.SelectionRequired,
                null,
                "preset_selection_required");
        }

        if (selectionVersion != 1)
        {
            return new SessionPermissionResult(
                SessionPermissionOutcome.PresetMismatch,
                null,
                "preset_mismatch");
        }

        var created = new AgentSessionPermissionSelection(
            sessionId,
            AgentPermissionPreset.Standard,
            1,
            now);
        await InsertAsync(connection, transaction, created, cancellationToken)
            .ConfigureAwait(false);
        return new SessionPermissionResult(SessionPermissionOutcome.CreatedDefault, created);
    }

    private static async Task<AgentSessionPermissionSelection?> GetInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        SessionId sessionId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT preset, permission_profile_version, version, updated_at_utc
            FROM agent_session_settings
            WHERE session_id = $session_id
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (!AgentPermissionPresetMapper.TryParseWireValue(reader.GetString(0), out var preset))
        {
            throw new HarnessStoreException("harness_session_preset_invalid");
        }

        return new AgentSessionPermissionSelection(
            sessionId,
            preset,
            reader.GetInt64(2),
            DateTimeOffset.Parse(
                reader.GetString(3),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind),
            reader.GetInt32(1));
    }

    private sealed record SessionPresetSnapshotRow(
        string SnapshotToken,
        string SessionId,
        bool HadPreviousSelection,
        AgentSessionPermissionSelection? PreviousSelection,
        AgentPermissionPreset AppliedPreset,
        int AppliedPermissionProfileVersion,
        long AppliedVersion,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ConsumedAt);

    private static async Task DeleteExpiredSnapshotsInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM agent_session_preset_snapshots WHERE expires_at_utc <= $now";
        command.Parameters.AddWithValue("$now", Utc(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> SnapshotExistsInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotToken,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM agent_session_preset_snapshots WHERE snapshot_token = $snapshot_token LIMIT 1";
        command.Parameters.AddWithValue("$snapshot_token", snapshotToken);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<long> CountSnapshotsInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM agent_session_preset_snapshots WHERE expires_at_utc > $now";
        command.Parameters.AddWithValue("$now", Utc(now));
        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);
    }

    private static async Task InsertSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotToken,
        SessionId sessionId,
        AgentSessionPermissionSelection? previous,
        AgentSessionPermissionSelection applied,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        HarnessSqliteWriteSet.ValidateSessionPresetSnapshotWrite(snapshotToken, previous, applied);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_session_preset_snapshots(
                snapshot_token,
                session_id,
                had_previous_selection,
                previous_preset,
                previous_permission_profile_version,
                previous_version,
                previous_updated_at_utc,
                applied_preset,
                applied_permission_profile_version,
                applied_version,
                expires_at_utc,
                consumed_at_utc,
                created_at_utc)
            VALUES (
                $snapshot_token,
                $session_id,
                $had_previous_selection,
                $previous_preset,
                $previous_permission_profile_version,
                $previous_version,
                $previous_updated_at_utc,
                $applied_preset,
                $applied_permission_profile_version,
                $applied_version,
                $expires_at_utc,
                NULL,
                $created_at_utc)
            """;
        command.Parameters.AddWithValue("$snapshot_token", snapshotToken);
        command.Parameters.AddWithValue("$session_id", sessionId.Value);
        command.Parameters.AddWithValue("$had_previous_selection", previous is null ? 0 : 1);
        AddNullable(command, "$previous_preset", previous is null ? null : AgentPermissionPresetMapper.ToWireValue(previous.Preset));
        AddNullable(command, "$previous_permission_profile_version", previous?.PermissionProfileVersion);
        AddNullable(command, "$previous_version", previous?.Version);
        AddNullable(command, "$previous_updated_at_utc", previous is null ? null : Utc(previous.UpdatedAt));
        command.Parameters.AddWithValue("$applied_preset", AgentPermissionPresetMapper.ToWireValue(applied.Preset));
        command.Parameters.AddWithValue("$applied_permission_profile_version", applied.PermissionProfileVersion);
        command.Parameters.AddWithValue("$applied_version", applied.Version);
        command.Parameters.AddWithValue("$expires_at_utc", Utc(now.Add(SessionPresetSnapshotLifetime)));
        command.Parameters.AddWithValue("$created_at_utc", Utc(now));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SessionPresetSnapshotRow?> GetSnapshotInTransactionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotToken,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT session_id,
                had_previous_selection,
                previous_preset,
                previous_permission_profile_version,
                previous_version,
                previous_updated_at_utc,
                applied_preset,
                applied_permission_profile_version,
                applied_version,
                expires_at_utc,
                consumed_at_utc
            FROM agent_session_preset_snapshots
            WHERE snapshot_token = $snapshot_token
            """;
        command.Parameters.AddWithValue("$snapshot_token", snapshotToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var hadPrevious = reader.GetInt32(1) == 1;
        AgentSessionPermissionSelection? previous = null;
        if (hadPrevious)
        {
            previous = new AgentSessionPermissionSelection(
                new SessionId(reader.GetString(0)),
                ReadPreset(reader.GetString(2)),
                reader.GetInt64(4),
                ParseTimestamp(reader.GetString(5)),
                reader.GetInt32(3));
        }

        return new SessionPresetSnapshotRow(
            snapshotToken,
            reader.GetString(0),
            hadPrevious,
            previous,
            ReadPreset(reader.GetString(6)),
            reader.GetInt32(7),
            reader.GetInt64(8),
            ParseTimestamp(reader.GetString(9)),
            reader.IsDBNull(10) ? null : ParseTimestamp(reader.GetString(10)));
    }

    private static async Task<int> RestoreSnapshotSelectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionPresetSnapshotRow snapshot,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (snapshot.HadPreviousSelection)
        {
            command.CommandText = """
                UPDATE agent_session_settings
                SET preset = $preset,
                    permission_profile_version = $permission_profile_version,
                    version = $version,
                    updated_at_utc = $updated_at_utc
                WHERE session_id = $session_id
                  AND version = $applied_version
                  AND preset = $applied_preset
                  AND permission_profile_version = $applied_permission_profile_version
                """;
            AddParameters(command, snapshot.PreviousSelection!);
            command.Parameters.AddWithValue("$applied_version", snapshot.AppliedVersion);
            command.Parameters.AddWithValue("$applied_preset", AgentPermissionPresetMapper.ToWireValue(snapshot.AppliedPreset));
            command.Parameters.AddWithValue("$applied_permission_profile_version", snapshot.AppliedPermissionProfileVersion);
        }
        else
        {
            command.CommandText = """
                DELETE FROM agent_session_settings
                WHERE session_id = $session_id
                  AND version = $applied_version
                  AND preset = $applied_preset
                  AND permission_profile_version = $applied_permission_profile_version
                """;
            command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
            command.Parameters.AddWithValue("$applied_version", snapshot.AppliedVersion);
            command.Parameters.AddWithValue("$applied_preset", AgentPermissionPresetMapper.ToWireValue(snapshot.AppliedPreset));
            command.Parameters.AddWithValue("$applied_permission_profile_version", snapshot.AppliedPermissionProfileVersion);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> MarkSnapshotConsumedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SessionPresetSnapshotRow snapshot,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        HarnessSqliteWriteSet.ValidateSessionPresetSnapshotConsume(
            snapshot.SnapshotToken,
            snapshot.SessionId,
            snapshot.AppliedPreset,
            snapshot.AppliedPermissionProfileVersion,
            snapshot.AppliedVersion);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_session_preset_snapshots
            SET consumed_at_utc = $consumed_at_utc
            WHERE snapshot_token = $snapshot_token
              AND session_id = $session_id
              AND applied_preset = $applied_preset
              AND applied_permission_profile_version = $applied_permission_profile_version
              AND applied_version = $applied_version
              AND consumed_at_utc IS NULL
            """;
        command.Parameters.AddWithValue("$consumed_at_utc", Utc(now));
        command.Parameters.AddWithValue("$snapshot_token", snapshot.SnapshotToken);
        command.Parameters.AddWithValue("$session_id", snapshot.SessionId);
        command.Parameters.AddWithValue("$applied_preset", AgentPermissionPresetMapper.ToWireValue(snapshot.AppliedPreset));
        command.Parameters.AddWithValue("$applied_permission_profile_version", snapshot.AppliedPermissionProfileVersion);
        command.Parameters.AddWithValue("$applied_version", snapshot.AppliedVersion);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteSnapshotAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string snapshotToken,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM agent_session_preset_snapshots WHERE snapshot_token = $snapshot_token";
        command.Parameters.AddWithValue("$snapshot_token", snapshotToken);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool SnapshotMatchesApplied(
        SessionPresetSnapshotRow snapshot,
        AgentSessionPermissionSelection? current)
    {
        return current is not null
            && current.Preset == snapshot.AppliedPreset
            && current.PermissionProfileVersion == snapshot.AppliedPermissionProfileVersion
            && current.Version == snapshot.AppliedVersion;
    }

    private static bool SnapshotMatchesPrevious(
        SessionPresetSnapshotRow snapshot,
        AgentSessionPermissionSelection? current)
    {
        if (!snapshot.HadPreviousSelection)
        {
            return current is null;
        }

        return current is not null
            && current.Preset == snapshot.PreviousSelection!.Preset
            && current.PermissionProfileVersion == snapshot.PreviousSelection.PermissionProfileVersion
            && current.Version == snapshot.PreviousSelection.Version
            && current.UpdatedAt == snapshot.PreviousSelection.UpdatedAt;
    }

    private static AgentPermissionPreset ReadPreset(string value)
    {
        if (!AgentPermissionPresetMapper.TryParseWireValue(value, out var preset))
        {
            throw new HarnessStoreException("harness_session_preset_invalid");
        }

        return preset;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static void AddNullable(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    public static bool IsValidSnapshotToken(string? snapshotToken)
    {
        return snapshotToken is { Length: 36 }
            && snapshotToken.StartsWith("sps_", StringComparison.Ordinal)
            && IsLowerHex(snapshotToken.AsSpan(4));
    }

    private static void ValidateSnapshotToken(string snapshotToken)
    {
        if (!IsValidSnapshotToken(snapshotToken))
        {
            throw new ArgumentOutOfRangeException(nameof(snapshotToken));
        }
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentSessionPermissionSelection selection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agent_session_settings(
                session_id,
                preset,
                permission_profile_version,
                version,
                updated_at_utc)
            VALUES (
                $session_id,
                $preset,
                $permission_profile_version,
                $version,
                $updated_at_utc)
            """;
        AddParameters(command, selection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AgentSessionPermissionSelection selection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE agent_session_settings
            SET preset = $preset,
                permission_profile_version = $permission_profile_version,
                version = $version,
                updated_at_utc = $updated_at_utc
            WHERE session_id = $session_id
            """;
        AddParameters(command, selection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameters(
        SqliteCommand command,
        AgentSessionPermissionSelection selection)
    {
        command.Parameters.AddWithValue("$session_id", selection.SessionId.Value);
        command.Parameters.AddWithValue("$preset", AgentPermissionPresetMapper.ToWireValue(selection.Preset));
        command.Parameters.AddWithValue(
            "$permission_profile_version",
            selection.PermissionProfileVersion);
        command.Parameters.AddWithValue("$version", selection.Version);
        command.Parameters.AddWithValue("$updated_at_utc", selection.UpdatedAt.ToString("O"));
    }

    private static void Validate(
        SessionId sessionId,
        AgentPermissionPreset preset,
        long selectionVersion)
    {
        _ = sessionId.Value;
        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        if (selectionVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(selectionVersion));
        }
    }
}
