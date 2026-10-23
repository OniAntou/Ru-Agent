using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace RuAgent.SessionInterchange;

/// <summary>One of opencode's sessions, as its own database describes it.</summary>
public sealed record OpenCodeSessionRef(string Id, string? Title, DateTimeOffset UpdatedAt);

/// <summary>Reads the supported v1 session schema from opencode's SQLite database.</summary>
/// <remarks>
/// The v1 session table stores metadata in scalar columns. Message and part rows store their
/// respective records as JSON in <c>data</c>. This reader is read-only because opencode owns
/// the file; newer schema generations fail closed until their mapping is implemented.
/// </remarks>
public static class OpenCodeDatabase
{
    private const int MaximumDatabasePathLength = 4096;
    private const int MaximumSessionIdLength = 256;
    private const int MaximumSessionLimit = 100;

    /// <summary>Where opencode keeps its database on Windows.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "opencode", "opencode.db");

    /// <summary>The sessions opencode knows about, most recently updated first.</summary>
    /// <exception cref="InvalidDataException">The file is not a supported opencode v1 store.</exception>
    public static IReadOnlyList<OpenCodeSessionRef> ListSessions(string databasePath, int limit = 20)
    {
        ValidateDatabasePath(databasePath);
        if (limit is < 1 or > MaximumSessionLimit)
            throw new InvalidDataException("opencode_input_invalid: session limit is outside the supported range.");

        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, time_updated FROM session ORDER BY time_updated DESC, id LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);

        var sessions = new List<OpenCodeSessionRef>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(id)) continue;
            sessions.Add(new OpenCodeSessionRef(id,
                reader.IsDBNull(1) ? null : reader.GetString(1), ReadUnixMilliseconds(reader.GetInt64(2))));
        }

        return sessions;
    }

    /// <summary>Returns a JSON envelope in the shape consumed by <see cref="OpenCodeSessionFormat"/>.</summary>
    /// <exception cref="InvalidDataException">The file is unsupported, oversized, or has no such session.</exception>
    public static string ReadSessionPayload(string databasePath, string sessionId)
    {
        ValidateDatabasePath(databasePath);
        ValidateSessionId(sessionId);
        using var connection = Open(databasePath);

        var session = ReadSession(connection, sessionId)
            ?? throw new InvalidDataException($"opencode_session_missing: session '{sessionId}' is not present.");

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

        var payload = new JsonObject { ["session"] = session, ["messages"] = messages };
        var json = payload.ToJsonString();
        if (Encoding.UTF8.GetByteCount(json) > SessionInterchangeLimits.MaxImportContentBytes)
            throw new InvalidDataException("opencode_session_too_large: the session exceeds the supported import size.");
        return json;
    }

    private static JsonObject? ReadSession(SqliteConnection connection, string sessionId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, directory, time_created, time_updated FROM session WHERE id = $id";
        command.Parameters.AddWithValue("$id", sessionId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new JsonObject
        {
            ["id"] = reader.GetString(0),
            ["title"] = reader.IsDBNull(1) ? null : reader.GetString(1),
            ["directory"] = reader.IsDBNull(2) ? null : reader.GetString(2),
            ["time"] = new JsonObject
            {
                ["created"] = reader.GetInt64(3),
                ["updated"] = reader.GetInt64(4),
            },
        };
    }

    private static SqliteConnection Open(string databasePath)
    {
        if (!File.Exists(databasePath))
            throw new InvalidDataException("opencode_database_missing: the database file does not exist.");

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString());
        try
        {
            connection.Open();
            ValidateSchema(connection, databasePath);
            return connection;
        }
        catch (SqliteException)
        {
            connection.Dispose();
            throw new InvalidDataException("opencode_database_unreadable: the file is not a readable SQLite database.");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void ValidateSchema(SqliteConnection connection, string databasePath)
    {
        var tables = ReadTableColumns(connection);
        if (tables.ContainsKey("session_v2")
            || !tables.TryGetValue("session", out var session)
            || !tables.TryGetValue("message", out var message)
            || !tables.TryGetValue("part", out var part))
        {
            throw UnsupportedSchema(databasePath);
        }

        if (!HasColumns(session, "id", "title", "directory", "time_created", "time_updated")
            || !HasColumns(message, "id", "session_id", "time_created", "data")
            || !HasColumns(part, "id", "message_id", "data"))
        {
            throw UnsupportedSchema(databasePath);
        }
    }

    private static Dictionary<string, HashSet<string>> ReadTableColumns(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('session', 'session_v2', 'message', 'part')";
        var tables = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read()) tables.Add(reader.GetString(0), new HashSet<string>(StringComparer.Ordinal));
        }

        foreach (var table in tables.Keys)
        {
            using var columns = connection.CreateCommand();
            // Table names are drawn from the fixed set returned above, never caller input.
            columns.CommandText = $"PRAGMA table_info({table})";
            using var reader = columns.ExecuteReader();
            while (reader.Read()) tables[table].Add(reader.GetString(1));
        }

        return tables;
    }

    private static bool HasColumns(HashSet<string> actual, params string[] required) =>
        required.All(actual.Contains);

    private static InvalidDataException UnsupportedSchema(string path) =>
        new($"opencode_schema_unsupported: '{Path.GetFileName(path)}' does not match the supported v1 session, message and part schema.");

    private static IEnumerable<(string Id, JsonNode Data)> ReadMany(
        SqliteConnection connection, string sql, string parameter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", parameter);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (Parse(reader.GetString(1)) is { } data) yield return (reader.GetString(0), data);
        }
    }

    private static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static DateTimeOffset ReadUnixMilliseconds(long milliseconds)
    {
        try { return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds); }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidDataException("opencode_schema_unsupported: session timestamps are outside the supported range.");
        }
    }

    private static void ValidateDatabasePath(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        if (databasePath.Length > MaximumDatabasePathLength)
            throw new InvalidDataException("opencode_input_invalid: database path exceeds the supported length.");
    }

    private static void ValidateSessionId(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (sessionId.Length > MaximumSessionIdLength
            || sessionId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-')))
            throw new InvalidDataException("opencode_input_invalid: session id is outside the supported syntax or length.");
    }
}
