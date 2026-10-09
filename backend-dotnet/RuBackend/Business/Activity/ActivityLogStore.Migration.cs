using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using RuBackend.Diagnostics;

namespace RuBackend.Business.Activity;

public sealed partial class ActivityLogStore
{
    private async Task<SqliteConnection> OpenStoreAsync(CancellationToken cancellationToken)
    {
        var connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_isMigrated)
            {
                using var transaction = connection.BeginTransaction(deferred: false);
                if (!await BusinessSqliteDatabase.HasMigratedAsync(connection, transaction, "activity", cancellationToken).ConfigureAwait(false))
                {
                    foreach (var path in LegacyActivityFiles())
                        await ImportLegacyActivityAsync(connection, transaction, path, cancellationToken).ConfigureAwait(false);
                    await TrimAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                    await BusinessSqliteDatabase.MarkMigratedAsync(connection, transaction, "activity", cancellationToken).ConfigureAwait(false);
                }
                transaction.Commit();
                _isMigrated = true;
            }
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private IEnumerable<string> LegacyActivityFiles()
    {
        yield return _paths.ActivityLogFile;
        if (Directory.Exists(_paths.LogRoot))
        {
            foreach (var path in Directory.EnumerateFiles(_paths.LogRoot, "activity_outbox.*.processing").Order(StringComparer.Ordinal))
                yield return path;
            yield return Path.Combine(_paths.LogRoot, "activity_outbox.jsonl");
        }
    }

    private async Task ImportLegacyActivityAsync(
        SqliteConnection connection, SqliteTransaction transaction, string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        using var reader = new BoundedTextLineReader(new StreamReader(stream, Encoding.UTF8, true), MaximumRecordBytes);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line.EndOfFile) break;
            if (line.TooLarge) continue;
            var activity = ParseEvent(line.Line!);
            if (activity is not null)
                await InsertAsync(connection, transaction, ActivityPreview.SanitizeEvent(activity, _clock), cancellationToken).ConfigureAwait(false);
        }
    }

    private static ActivityEvent? ParseEvent(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(line) is not JsonObject root ||
                !TryReadNumber(root["ts"], out var timestamp) ||
                !TryReadString(root["iso"], out var iso) ||
                !TryReadString(root["type"], out var type))
            {
                return null;
            }

            string? eventId = null;
            if (root["event_id"] is JsonValue eventIdValue &&
                eventIdValue.TryGetValue<string>(out var parsedEventId))
            {
                eventId = parsedEventId;
            }

            string? sessionId = null;
            if (root["session_id"] is JsonValue sessionValue &&
                sessionValue.TryGetValue<string>(out var parsedSession))
            {
                sessionId = parsedSession;
            }

            var data = root["data"] is JsonObject dataObject
                ? dataObject
                : [];

            return new ActivityEvent(eventId, timestamp, iso, type, sessionId, data);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        if (node is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var parsed))
        {
            value = parsed;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadNumber(JsonNode? node, out double value)
    {
        if (node is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<int>(out var integer))
            {
                value = integer;
                return true;
            }

            if (jsonValue.TryGetValue<long>(out var longInteger))
            {
                value = longInteger;
                return true;
            }

            if (jsonValue.TryGetValue<double>(out value) && double.IsFinite(value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }
}
