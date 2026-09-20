using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.SessionInterchange;

/// <summary>
/// opencode's conversation store, as a payload: the session record and its messages, each
/// with the parts that make it up, in the same JSON opencode keeps in its database.
/// </summary>
/// <remarks>
/// The payload exists because opencode moved its store into SQLite (see
/// <see cref="OpenCodeDatabase"/>), so there is no session *file* to read. Keeping the
/// database on one side of this seam and the JSON on the other means the format stays pure
/// — it can be exercised with a literal string — while exactly one class knows how to talk
/// to somebody else's database.
/// </remarks>
public sealed class OpenCodeSessionFormat : ISessionFormat
{
    /// <inheritdoc />
    public string Id => "opencode";

    /// <inheritdoc />
    public string DisplayName => "opencode";

    /// <summary>
    /// opencode is read-only here, and the reason is about verification rather than effort.
    /// </summary>
    /// <remarks>
    /// Writing a session would mean inserting rows into opencode's own SQLite database with
    /// its bookkeeping columns, ids and migration level. The only honest way to know those
    /// inserts are right is to try them against a real install, and Ru will not do that to
    /// somebody's live projects database on a guess — a wrong insert is not a failed import,
    /// it is a damaged store.
    /// </remarks>
    public bool CanWrite => false;

    /// <inheritdoc />
    public string WriteRefusal =>
        "opencode keeps sessions in its own SQLite database with its own bookkeeping, and Ru will not insert into it without having verified the shape against a real install.";

    /// <inheritdoc />
    public SessionTranscript Read(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        JsonNode? node = TryParse(payload);
        if (node is not JsonObject root)
        {
            throw new InvalidDataException("This payload is not JSON, so it is not an opencode session.");
        }

        if (root["session"] is not JsonObject session)
        {
            throw new InvalidDataException("This payload has no session record, so it is not an opencode session.");
        }

        var entries = new List<TranscriptEntry>();
        if (root["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonObject>())
            {
                entries.AddRange(ReadMessage(message));
            }
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException("This opencode session has no readable turns.");
        }

        var createdAt = ReadTime(session, "created");
        var title = ReadString(session, "title");
        if (string.IsNullOrWhiteSpace(title) || title.StartsWith("New session", StringComparison.OrdinalIgnoreCase))
        {
            // opencode seeds a placeholder name and replaces it later; the first thing the
            // person asked says more than the date they asked it.
            title = TranscriptText.TitleFrom(entries);
        }

        return new SessionTranscript(
            ReadString(session, "id") ?? Guid.NewGuid().ToString(),
            title,
            ReadString(session, "directory"),
            createdAt == default ? entries[0].Timestamp : createdAt,
            entries);
    }

    /// <summary>
    /// Nothing is written in this format, so this refuses rather than inserting into a store
    /// it does not own.
    /// </summary>
    /// <exception cref="NotSupportedException">Always.</exception>
    public IReadOnlyList<SessionOutputFile> Write(SessionTranscript transcript) =>
        throw new NotSupportedException(WriteRefusal);

    private static IReadOnlyList<TranscriptEntry> ReadMessage(JsonObject message)
    {
        if (message["data"] is not JsonObject data)
        {
            return [];
        }

        var role = ReadString(data, "role") == "assistant"
            ? TranscriptEntryKind.AssistantMessage
            : TranscriptEntryKind.UserMessage;
        var timestamp = ReadTime(data, "created");
        var parts = message["parts"] as JsonArray ?? [];

        var entries = new List<TranscriptEntry>();
        var text = new List<string>();

        // Prose is written when something else interrupts it rather than up front: opencode
        // stores a turn's reasoning, calls and words in the order they happened, and a reader
        // that hoisted the words to the top would be telling the turn backwards.
        void FlushText()
        {
            if (text.Count == 0)
            {
                return;
            }

            entries.Add(new TranscriptEntry(role, timestamp, Text: string.Join('\n', text)));
            text.Clear();
        }

        foreach (var part in parts.OfType<JsonObject>())
        {
            switch (ReadString(part, "type"))
            {
                case "text" when ReadString(part, "text") is { Length: > 0 } spoken:
                    text.Add(spoken);
                    break;

                case "reasoning" when ReadString(part, "text") is { Length: > 0 } thinking:
                    FlushText();
                    entries.Add(new TranscriptEntry(TranscriptEntryKind.Reasoning, timestamp, Text: thinking));
                    break;

                case "tool":
                    FlushText();
                    entries.AddRange(ReadToolPart(part, timestamp));
                    break;

                default:
                    FlushText();
                    break;
            }
        }

        FlushText();
        return entries;
    }

    private static IEnumerable<TranscriptEntry> ReadToolPart(JsonObject part, DateTimeOffset timestamp)
    {
        var name = ReadString(part, "tool");
        var callId = ReadString(part, "callID");
        var state = part["state"] as JsonObject;
        var status = state is null ? null : ReadString(state, "status");

        // A tool part is one row that fills in as it goes: a call that has not started has
        // nothing to report yet, and one that has keeps both halves of the exchange.
        if (status is "completed" or "running" or "error")
        {
            yield return new TranscriptEntry(
                TranscriptEntryKind.ToolCall,
                timestamp,
                ToolName: name,
                ToolCallId: callId,
                ToolArguments: state?["input"]?.ToJsonString());
        }

        if (status is "completed" or "error" && state is not null)
        {
            yield return new TranscriptEntry(
                TranscriptEntryKind.ToolResult,
                timestamp,
                ToolCallId: callId,
                ToolResult: state["output"] is JsonValue value && value.TryGetValue(out string? output)
                    ? output
                    : state["error"]?.ToJsonString());
        }
    }

    private static JsonNode? TryParse(string payload)
    {
        try
        {
            return JsonNode.Parse(payload);
        }
        catch (JsonException)
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
