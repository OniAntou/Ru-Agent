using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace RuAgent.SessionInterchange;

/// <summary>One of opencode's sessions, as its own database describes it.</summary>
public sealed record OpenCodeSessionRef(string Id, string? Title, DateTimeOffset UpdatedAt);

/// <summary>
/// Reads a session out of opencode's SQLite database.
/// </summary>
/// <remarks>
/// This is the only class in this project that touches somebody else's storage, and it opens
/// the database <b>read-only</b>: opencode owns that file. A reader that took a write lock
/// would be able to stop the app it is reading from.
///
/// Nothing here writes, and that is a decision rather than an omission — see
/// <see cref="OpenCodeSessionFormat.WriteRefusal"/>.
/// </remarks>
public static class OpenCodeDatabase
{
    /// <summary>Where opencode keeps its database on Windows.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local",
        "share",
        "opencode",
        "opencode.db");

    /// <summary>The sessions opencode knows about, most recently updated first.</summary>
    /// <exception cref="InvalidDataException">The file is not an opencode store.</exception>
    public static IReadOnlyList<OpenCodeSessionRef> ListSessions(string databasePath, int limit = 20)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        using var connection = Open(databasePath);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM session ORDER BY time_updated DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var sessions = new List<OpenCodeSessionRef>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Parse(reader.GetString(0)) is not JsonObject session)
            {
                continue;
            }

            var id = ReadString(session, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            sessions.Add(new OpenCodeSessionRef(
                id,
                ReadString(session, "title"),
                ReadTime(session, "updated")));
        }

        return sessions;
    }

    /// <summary>
    /// One session as the payload <see cref="OpenCodeSessionFormat"/> reads.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is not an opencode store, or holds no such session.</exception>
    public static string ReadSessionPayload(string databasePath, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var connection = Open(databasePath);

        var session = ReadSingle(connection, "SELECT data FROM session WHERE id = $id", sessionId)
            ?? throw new InvalidDataException($"opencode has no session '{sessionId}' in this database.");

        var messages = new JsonArray();
        foreach (var (messageId, messageData) in ReadMany(
            connection,
            "SELECT id, data FROM message WHERE session_id = $id ORDER BY time_created, id",
            sessionId))
        {
            var parts = new JsonArray();
            foreach (var (_, partData) in ReadMany(
                connection,
                "SELECT id, data FROM part WHERE message_id = $id ORDER BY id",
                messageId))
            {
                parts.Add(partData);
            }

            messages.Add(new JsonObject { ["data"] = messageData, ["parts"] = parts });
        }

        return new JsonObject { ["session"] = session, ["messages"] = messages }.ToJsonString();
    }

    private static SqliteConnection Open(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            throw new InvalidDataException($"There is no opencode database at '{databasePath}'.");
        }

        // Read-only, and not only for tidiness: this file belongs to a running application.
        var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly");
        connection.Open();

        try
        {
            using var check = connection.CreateCommand();
            check.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('session', 'message', 'part')";
            var found = 0;
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                found++;
            }

            if (found < 3)
            {
                throw new InvalidDataException(
                    $"'{databasePath}' is a SQLite database but not an opencode store: it is missing the session, message or part table.");
            }
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        return connection;
    }

    private static JsonNode? ReadSingle(SqliteConnection connection, string sql, string parameter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", parameter);
        var value = command.ExecuteScalar();
        return value is string text ? Parse(text) : null;
    }

    private static IEnumerable<(string Id, JsonNode Data)> ReadMany(
        SqliteConnection connection,
        string sql,
        string parameter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", parameter);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Parse(reader.GetString(1)) is { } data)
            {
                yield return (reader.GetString(0), data);
            }
        }
    }

    private static JsonNode? Parse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonObject record, string property) =>
        record[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static DateTimeOffset ReadTime(JsonObject record, string property) =>
        record["time"] is JsonObject time
            && time[property] is JsonValue value
            && value.TryGetValue(out long milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : default;
}
