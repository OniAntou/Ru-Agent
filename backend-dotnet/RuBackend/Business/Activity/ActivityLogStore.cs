using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace RuBackend.Business.Activity;

public sealed partial class ActivityLogStore : IActivityLogStore
{
    private const int MaximumRead = 500;
    private const int MaximumEntries = 5_000;
    private const int MaximumRecordBytes = 256_000;
    private readonly DataPaths _paths;
    private readonly BusinessSqliteDatabase _database;
    private readonly TimeProvider _clock;
    private volatile bool _isMigrated;

    public ActivityLogStore(DataPaths paths, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
        _database = new BusinessSqliteDatabase(paths);
        _clock = clock ?? TimeProvider.System;
    }

    public async Task AppendAsync(ActivityEvent activity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activity);
        var sanitized = ActivityPreview.SanitizeEvent(activity, _clock);
        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        using var transaction = connection.BeginTransaction(deferred: false);
        await InsertAsync(connection, transaction, sanitized, cancellationToken).ConfigureAwait(false);
        await TrimAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
        transaction.Commit();
    }

    public async Task<IReadOnlyList<ActivityEvent>> ReadAsync(int limit, CancellationToken cancellationToken)
    {
        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT document FROM (
                SELECT document, timestamp, sequence FROM business_activity
                ORDER BY timestamp DESC, sequence DESC LIMIT $limit
            ) ORDER BY timestamp, sequence;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, MaximumRead));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = new List<ActivityEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var activity = ParseEvent(reader.GetString(0)) ?? throw new InvalidDataException("activity_record_invalid");
            events.Add(ActivityPreview.SanitizeEvent(activity, _clock));
        }
        return events;
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenStoreAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM business_activity;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertAsync(
        SqliteConnection connection, SqliteTransaction transaction, ActivityEvent activity, CancellationToken cancellationToken)
    {
        var document = JsonSerializer.Serialize(activity);
        if (System.Text.Encoding.UTF8.GetByteCount(document) > MaximumRecordBytes)
            throw new InvalidDataException("activity_record_too_large");
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO business_activity(event_id, timestamp, document) VALUES ($id, $timestamp, $document) ON CONFLICT(event_id) DO NOTHING;";
        command.Parameters.AddWithValue("$id", string.IsNullOrWhiteSpace(activity.EventId) ? DBNull.Value : activity.EventId);
        command.Parameters.AddWithValue("$timestamp", activity.Ts);
        command.Parameters.AddWithValue("$document", document);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task TrimAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM business_activity WHERE sequence <= (SELECT sequence FROM business_activity ORDER BY sequence DESC LIMIT 1 OFFSET $retained);";
        command.Parameters.AddWithValue("$retained", MaximumEntries);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
