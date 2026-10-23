using System.Text.Json;
using Microsoft.Data.Sqlite;
using RuAgent.SessionInterchange;
using Xunit;

namespace RuAgent.SessionInterchange.Tests;

/// <summary>
/// Exercises the opencode reader against a database built with the schema opencode itself
/// uses, so the mapping is checked without touching anybody's live store.
/// </summary>
public sealed class OpenCodeDatabaseTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ru-opencode-tests").FullName;
    private readonly OpenCodeSessionFormat _format = new();

    private string DatabasePath => Path.Combine(_directory, "opencode.db");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A test that leaves a temp directory behind is not worth failing a run over.
        }
    }

    [Fact]
    public void Reads_a_session_a_person_can_recognise()
    {
        Seed(
            session: """{"id":"ses_1","slug":"crisp-wizard","projectID":"global","directory":"C:\\Users\\Someone","title":"Review the codebase","time":{"created":1789903447113,"updated":1789903499000}}""",
            messages:
            [
                ("""{"role":"user","time":{"created":1789903447131}}""",
                [
                    """{"type":"text","text":"hãy đọc codebase"}""",
                ]),
                ("""{"role":"assistant","time":{"created":1789903447200}}""",
                [
                    """{"type":"reasoning","text":"Đọc trước rồi mới kết luận"}""",
                    """{"type":"tool","tool":"read","callID":"call_1","state":{"status":"completed","input":{"filePath":"C:\\LLM\\README.md"},"output":"# Ru"}}""",
                    """{"type":"text","text":"Đã đọc xong."}""",
                ]),
            ]);

        SessionTranscript transcript = _format.Read(OpenCodeDatabase.ReadSessionPayload(DatabasePath, "ses_1"));

        Assert.Equal("ses_1", transcript.Id);
        Assert.Equal("Review the codebase", transcript.Title);
        Assert.Equal("C:\\Users\\Someone", transcript.WorkingDirectory);
        Assert.Equal(
            [
                TranscriptEntryKind.UserMessage,
                TranscriptEntryKind.Reasoning,
                TranscriptEntryKind.ToolCall,
                TranscriptEntryKind.ToolResult,
                TranscriptEntryKind.AssistantMessage,
            ],
            transcript.Entries.Select(entry => entry.Kind));

        // opencode gives a tool part an id from the model, so the call and its output keep it.
        Assert.Equal("call_1", transcript.Entries[2].ToolCallId);
        Assert.Equal("call_1", transcript.Entries[3].ToolCallId);
        Assert.Equal("read", transcript.Entries[2].ToolName);
        Assert.Equal("# Ru", transcript.Entries[3].ToolResult);
    }

    [Fact]
    public void A_placeholder_title_gives_way_to_the_first_thing_asked()
    {
        Seed(
            session: """{"id":"ses_2","title":"New session - 2026-09-20T11:24:07.113Z","directory":"C:\\Users\\Someone","time":{"created":1789903447113,"updated":1789903499000}}""",
            messages:
            [
                ("""{"role":"user","time":{"created":1789903447131}}""", ["""{"type":"text","text":"dọn dẹp repo" }"""]),
            ]);

        Assert.Equal("dọn dẹp repo", _format.Read(OpenCodeDatabase.ReadSessionPayload(DatabasePath, "ses_2")).Title);
    }

    [Fact]
    public void Lists_the_sessions_newest_first()
    {
        Seed(
            session: """{"id":"ses_old","title":"Old","time":{"created":1000,"updated":2000}}""",
            messages: [("""{"role":"user","time":{"created":1001}}""", ["""{"type":"text","text":"cũ"}"""])]);
        AddSession("""{"id":"ses_new","title":"New","time":{"created":3000,"updated":4000}}""");

        var sessions = OpenCodeDatabase.ListSessions(DatabasePath);

        Assert.Equal(["ses_new", "ses_old"], sessions.Select(session => session.Id));
        Assert.Equal("New", sessions[0].Title);
    }

    [Fact]
    public void Refuses_a_sqlite_file_that_is_not_an_opencode_store()
    {
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (id TEXT PRIMARY KEY)";
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => OpenCodeDatabase.ReadSessionPayload(DatabasePath, "ses_1"));

        Assert.Contains("opencode_schema_unsupported", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_a_store_that_has_no_such_session()
    {
        Seed(
            session: """{"id":"ses_1","title":"One","time":{"created":1,"updated":2}}""",
            messages: [("""{"role":"user","time":{"created":1}}""", ["""{"type":"text","text":"hi"}"""])]);

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => OpenCodeDatabase.ReadSessionPayload(DatabasePath, "ses_missing"));

        Assert.Contains("opencode_session_missing", error.Message, StringComparison.Ordinal);
        Assert.Contains("ses_missing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_unsupported_session_v2_schema_with_a_bounded_named_error()
    {
        using (var connection = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE session_v2 (id TEXT PRIMARY KEY)";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<InvalidDataException>(
            () => OpenCodeDatabase.ListSessions(DatabasePath));

        Assert.Contains("opencode_schema_unsupported", error.Message, StringComparison.Ordinal);
        Assert.True(error.Message.Length < 256);
    }

    [Fact]
    public void Rejects_session_list_limits_outside_the_supported_range()
    {
        var error = Assert.Throws<InvalidDataException>(
            () => OpenCodeDatabase.ListSessions(DatabasePath, limit: 101));

        Assert.Contains("opencode_input_invalid", error.Message, StringComparison.Ordinal);
    }

    private void Seed(string session, IReadOnlyList<(string Message, IReadOnlyList<string> Parts)> messages) =>
        Seed(IdOf(session), session, UpdatedOf(session), messages);

    private void Seed(
        string id,
        string session,
        long updated,
        IReadOnlyList<(string Message, IReadOnlyList<string> Parts)> messages)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();

        using (var schema = connection.CreateCommand())
        {
            // Minimal v1 tables matching opencode's scalar session metadata and JSON
            // message/part payloads. In particular, session has no data column.
            schema.CommandText = """
                CREATE TABLE session (id TEXT PRIMARY KEY, project_id TEXT NOT NULL, slug TEXT NOT NULL,
                    directory TEXT NOT NULL, title TEXT NOT NULL, time_created INTEGER NOT NULL,
                    time_updated INTEGER NOT NULL, parent_id TEXT, summary TEXT, version TEXT NOT NULL);
                CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT NOT NULL,
                    time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL, data TEXT NOT NULL);
                CREATE TABLE part (id TEXT PRIMARY KEY, message_id TEXT NOT NULL, session_id TEXT NOT NULL,
                    time_created INTEGER NOT NULL, time_updated INTEGER NOT NULL, data TEXT NOT NULL);
                """;
            schema.ExecuteNonQuery();
        }

        WriteSession(connection, id, session, updated);

        var index = 0;
        foreach (var (message, parts) in messages)
        {
            var messageId = $"msg_{index++}";
            using var insertMessage = connection.CreateCommand();
            insertMessage.CommandText =
                "INSERT INTO message (id, session_id, time_created, time_updated, data) VALUES ($id, $session, 1, 1, $data)";
            insertMessage.Parameters.AddWithValue("$id", messageId);
            insertMessage.Parameters.AddWithValue("$session", id);
            insertMessage.Parameters.AddWithValue("$data", message);
            insertMessage.ExecuteNonQuery();

            var partIndex = 0;
            foreach (var part in parts)
            {
                using var insertPart = connection.CreateCommand();
                insertPart.CommandText =
                    "INSERT INTO part (id, message_id, session_id, time_created, time_updated, data) VALUES ($id, $message, $session, 1, 1, $data)";
                insertPart.Parameters.AddWithValue("$id", $"prt_{index}_{partIndex++}");
                insertPart.Parameters.AddWithValue("$message", messageId);
                insertPart.Parameters.AddWithValue("$session", id);
                insertPart.Parameters.AddWithValue("$data", part);
                insertPart.ExecuteNonQuery();
            }
        }
    }

    private void AddSession(string session) => AddSession(IdOf(session), session, UpdatedOf(session));

    private void AddSession(string id, string session, long updated)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        connection.Open();
        WriteSession(connection, id, session, updated);
    }

    /// <summary>
    /// Writes v1 session scalar metadata. The database keeps no JSON session payload;
    /// only message and part rows have a data column.
    /// </summary>
    private static void WriteSession(SqliteConnection connection, string id, string session, long updated)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session (id, project_id, slug, directory, title, time_created, time_updated, version)
            VALUES ($id, 'global', 'slug', $directory, $title, $created, $updated, '1.18.31')
            """;
        using var document = JsonDocument.Parse(session);
        var record = document.RootElement;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$directory", record.TryGetProperty("directory", out var directory)
            ? directory.GetString()! : "C:\\Users\\Someone");
        command.Parameters.AddWithValue("$title", record.TryGetProperty("title", out var title)
            ? title.GetString()! : "");
        command.Parameters.AddWithValue("$created", record.GetProperty("time").GetProperty("created").GetInt64());
        command.Parameters.AddWithValue("$updated", updated);
        command.ExecuteNonQuery();
    }

    private static string IdOf(string session) =>
        JsonDocument.Parse(session).RootElement.GetProperty("id").GetString()!;

    private static long UpdatedOf(string session) =>
        JsonDocument.Parse(session).RootElement.GetProperty("time").GetProperty("updated").GetInt64();
}
