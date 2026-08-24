using Microsoft.Data.Sqlite;
using RuAgent.Harness.Core;

namespace RuAgent.Harness.Persistence.Policy;

public sealed record AgentSessionPermissionSelection(
    SessionId SessionId,
    AgentPermissionPreset Preset,
    long Version,
    DateTimeOffset UpdatedAt);

public enum SessionPermissionOutcome
{
    CreatedDefault,
    Matched,
    PresetMismatch,
    SelectionRequired,
    UpdatedInteractively,
    VersionConflict,
}

public readonly record struct SessionPermissionResult(
    SessionPermissionOutcome Outcome,
    AgentSessionPermissionSelection? Selection,
    string? ErrorCode = null);

public sealed class AgentSessionPermissionStore
{
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
                    created);
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
            };
            await UpdateAsync(connection, transaction, updated, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new SessionPermissionResult(
                SessionPermissionOutcome.UpdatedInteractively,
                updated);
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
            return existing.Preset == preset && existing.Version == selectionVersion
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
            SELECT preset, version, updated_at_utc
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

        return new AgentSessionPermissionSelection(
            sessionId,
            (AgentPermissionPreset)reader.GetInt32(0),
            reader.GetInt64(1),
            DateTimeOffset.Parse(
                reader.GetString(2),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind));
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
            INSERT INTO agent_session_settings(session_id, preset, version, updated_at_utc)
            VALUES ($session_id, $preset, $version, $updated_at_utc)
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
            SET preset = $preset, version = $version, updated_at_utc = $updated_at_utc
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
        command.Parameters.AddWithValue("$preset", (int)selection.Preset);
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
