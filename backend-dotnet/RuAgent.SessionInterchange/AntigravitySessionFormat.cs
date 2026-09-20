using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.SessionInterchange;

/// <summary>
/// Antigravity's conversation log: one JSONL file per conversation at
/// <c>~/.gemini/antigravity*/brain/&lt;conversationId&gt;/.system_generated/logs/transcript.jsonl</c>.
/// </summary>
/// <remarks>
/// This format is read-only, and not because writing it would be tedious: Antigravity's
/// canonical conversation store is a protobuf payload inside a per-conversation SQLite
/// database, and its <c>.proto</c> definitions are not on disk. The JSONL parsed here is the
/// log Antigravity writes beside that database — enough to bring a conversation into Ru,
/// not enough to hand one back in a shape the app would load.
///
/// Two things are lossy on the way in. A user turn arrives wrapped in
/// <c>&lt;USER_REQUEST&gt;</c> tags, which is Antigravity's envelope rather than something the
/// person typed, so it is unwrapped. And the log gives a tool call no id, so a call here
/// carries no id and its output is whatever the next non-model line holds: the order is the
/// pairing, which is all the log itself has.
/// </remarks>
public sealed class AntigravitySessionFormat : ISessionFormat
{
    /// <inheritdoc />
    public string Id => "antigravity";

    /// <inheritdoc />
    public string DisplayName => "Antigravity";

    /// <inheritdoc />
    public bool CanWrite => false;

    /// <inheritdoc />
    public string WriteRefusal =>
        "Antigravity keeps conversations as protobuf payloads in a per-conversation SQLite database whose schema is not on disk, so Ru can read a transcript but cannot produce one Antigravity would open.";

    /// <inheritdoc />
    public SessionTranscript Read(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var entries = new List<TranscriptEntry>();
        DateTimeOffset createdAt = default;
        var sawKnownLine = false;

        foreach (var line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? node = TryParse(line);
            if (node is not JsonObject record || ReadString(record, "type") is not { Length: > 0 } type)
            {
                continue;
            }

            sawKnownLine = true;
            var timestamp = TryReadTimestamp(record, out var stamped) ? stamped : createdAt;
            if (createdAt == default)
            {
                createdAt = timestamp;
            }

            switch (type)
            {
                case "USER_INPUT":
                    entries.Add(new TranscriptEntry(
                        TranscriptEntryKind.UserMessage,
                        timestamp,
                        Text: UnwrapUserRequest(ReadString(record, "content"))));
                    break;

                case "PLANNER_RESPONSE":
                    if (ReadString(record, "thinking") is { Length: > 0 } thinking)
                    {
                        entries.Add(new TranscriptEntry(TranscriptEntryKind.Reasoning, timestamp, Text: thinking));
                    }

                    if (ReadString(record, "content") is { Length: > 0 } text)
                    {
                        entries.Add(new TranscriptEntry(TranscriptEntryKind.AssistantMessage, timestamp, Text: text));
                    }

                    foreach (var call in ReadToolCalls(record, timestamp))
                    {
                        entries.Add(call);
                    }

                    break;

                case "GENERIC":
                    // The log splits a model's narration from a tool's output by which side
                    // wrote the line, which is the only thing that distinguishes them.
                    entries.Add(ReadString(record, "source") == "MODEL"
                        ? new TranscriptEntry(
                            TranscriptEntryKind.AssistantMessage,
                            timestamp,
                            Text: ReadString(record, "content"))
                        : new TranscriptEntry(
                            TranscriptEntryKind.ToolResult,
                            timestamp,
                            ToolResult: ReadString(record, "content")));
                    break;

                case "SYSTEM_MESSAGE":
                    entries.Add(new TranscriptEntry(
                        TranscriptEntryKind.System,
                        timestamp,
                        Text: ReadString(record, "content")));
                    break;
            }
        }

        if (!sawKnownLine)
        {
            throw new InvalidDataException(
                "This payload has none of the step types an Antigravity transcript has (USER_INPUT, PLANNER_RESPONSE, GENERIC, SYSTEM_MESSAGE).");
        }

        // A tool call carries neither text nor output, so a filter that only looked for
        // words would drop every call the conversation made.
        var readable = entries
            .Where(entry => entry.Kind == TranscriptEntryKind.ToolCall
                || !string.IsNullOrWhiteSpace(entry.Text)
                || !string.IsNullOrWhiteSpace(entry.ToolResult))
            .ToList();
        if (readable.Count == 0)
        {
            throw new InvalidDataException("This Antigravity transcript has no readable turns.");
        }

        return new SessionTranscript(
            Guid.NewGuid().ToString(),
            TranscriptText.TitleFrom(readable),
            WorkingDirectory: null,
            createdAt == default ? readable[0].Timestamp : createdAt,
            readable);
    }

    /// <summary>
    /// Nothing is written in this format, so this refuses rather than producing a file that
    /// looks like a trajectory.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public IReadOnlyList<SessionOutputFile> Write(SessionTranscript transcript) =>
        throw new NotSupportedException(WriteRefusal);

    private static IEnumerable<TranscriptEntry> ReadToolCalls(JsonObject record, DateTimeOffset timestamp)
    {
        if (record["tool_calls"] is not JsonArray calls)
        {
            yield break;
        }

        foreach (var call in calls.OfType<JsonObject>())
        {
            yield return new TranscriptEntry(
                TranscriptEntryKind.ToolCall,
                timestamp,
                ToolName: ReadString(call, "name"),
                ToolArguments: call["args"]?.ToJsonString());
        }
    }

    private static string? UnwrapUserRequest(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        const string open = "<USER_REQUEST>";
        const string close = "</USER_REQUEST>";
        var text = content.Trim();
        return text.StartsWith(open, StringComparison.Ordinal) && text.EndsWith(close, StringComparison.Ordinal)
            ? text[open.Length..^close.Length].Trim()
            : text;
    }

    private static JsonNode? TryParse(string line)
    {
        try
        {
            return JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonObject record, string property) =>
        record[property] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    private static bool TryReadTimestamp(JsonObject record, out DateTimeOffset timestamp)
    {
        timestamp = default;
        return ReadString(record, "created_at") is { Length: > 0 } text
            && DateTimeOffset.TryParse(text, out timestamp);
    }
}
