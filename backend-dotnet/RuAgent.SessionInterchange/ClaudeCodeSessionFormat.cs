using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.SessionInterchange;

/// <summary>
/// Claude Code's transcript format: one append-only JSONL file per session at
/// <c>~/.claude/projects/&lt;slugified cwd&gt;/&lt;sessionId&gt;.jsonl</c>, where each line is an
/// entry carrying its own <c>uuid</c> and a <c>parentUuid</c> pointing at the entry
/// before it.
/// </summary>
/// <remarks>
/// Only the structural envelope is written back, and that is deliberate: the lines
/// this format reads in the wild also carry <c>version</c>, <c>entrypoint</c>,
/// <c>userType</c>, <c>permissionMode</c> and <c>promptSource</c>, and every one of
/// those describes the install that produced the file. Copying this install's values
/// into a file meant for another would be inventing facts about that machine, so they
/// are left out and the partner fills in its own.
///
/// Tool blocks are written in the Anthropic content-block shape
/// (<c>tool_use</c> / <c>tool_result</c>) rather than one observed on this machine:
/// the single Claude Code session available here died at authentication before any
/// tool ran, so that shape is inferred from the documented message schema and is the
/// part of this adapter least backed by evidence.
/// </remarks>
public sealed class ClaudeCodeSessionFormat : ISessionFormat
{
    /// <inheritdoc />
    public string Id => "claude-code";

    /// <inheritdoc />
    public string DisplayName => "Claude Code";

    /// <inheritdoc />
    public bool CanWrite => true;

    /// <inheritdoc />
    public string WriteRefusal => string.Empty;

    /// <summary>File name Claude Code expects for a session.</summary>
    public static string FileNameFor(SessionTranscript transcript) => $"{ResolveId(transcript)}.jsonl";

    /// <inheritdoc />
    public SessionTranscript Read(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var entries = new List<TranscriptEntry>();
        string? sessionId = null;
        string? workingDirectory = null;
        DateTimeOffset createdAt = default;
        bool sawKnownLine = false;

        foreach (string line in SplitLines(payload))
        {
            JsonNode? node = TryParse(line);
            if (node is not JsonObject record)
            {
                continue;
            }

            string? type = ReadString(record, "type");
            if (type is not ("user" or "assistant" or "last-prompt"))
            {
                continue;
            }

            sawKnownLine = true;
            sessionId ??= ReadString(record, "sessionId");
            workingDirectory ??= ReadString(record, "cwd");
            if (createdAt == default && TryReadTimestamp(record, out DateTimeOffset stamped))
            {
                createdAt = stamped;
            }

            if (record["message"] is not JsonObject message)
            {
                continue;
            }

            DateTimeOffset timestamp = TryReadTimestamp(record, out DateTimeOffset entryStamp) ? entryStamp : createdAt;
            if (message["content"] is not JsonArray content)
            {
                continue;
            }

            foreach (JsonNode? block in content)
            {
                if (block is not JsonObject blockObject)
                {
                    continue;
                }

                string? blockType = ReadString(blockObject, "type");
                switch (blockType)
                {
                    case "text" when ReadString(blockObject, "text") is { Length: > 0 } text:
                        entries.Add(new TranscriptEntry(
                            type == "assistant" ? TranscriptEntryKind.AssistantMessage : TranscriptEntryKind.UserMessage,
                            timestamp,
                            Text: text));
                        break;

                    case "thinking" when ReadString(blockObject, "thinking") is { Length: > 0 } thinking:
                        entries.Add(new TranscriptEntry(TranscriptEntryKind.Reasoning, timestamp, Text: thinking));
                        break;

                    case "tool_use":
                        entries.Add(new TranscriptEntry(
                            TranscriptEntryKind.ToolCall,
                            timestamp,
                            ToolName: ReadString(blockObject, "name"),
                            ToolCallId: ReadString(blockObject, "id"),
                            ToolArguments: blockObject["input"]?.ToJsonString()));
                        break;

                    case "tool_result":
                        entries.Add(new TranscriptEntry(
                            TranscriptEntryKind.ToolResult,
                            timestamp,
                            ToolCallId: ReadString(blockObject, "tool_use_id"),
                            ToolResult: ReadToolResultText(blockObject)));
                        break;
                }
            }
        }

        if (!sawKnownLine)
        {
            throw new InvalidDataException(
                "This payload has none of the line types a Claude Code transcript has (user, assistant, last-prompt).");
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException("This Claude Code transcript has no readable turns.");
        }

        return new SessionTranscript(
            sessionId ?? Guid.NewGuid().ToString(),
            TranscriptText.TitleFrom(entries),
            workingDirectory,
            createdAt == default ? entries[0].Timestamp : createdAt,
            entries);
    }

    /// <inheritdoc />
    public IReadOnlyList<SessionOutputFile> Write(SessionTranscript transcript)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var lines = new List<string>();
        string previousUuid = string.Empty;
        bool first = true;
        string? lastPrompt = null;

        foreach (TranscriptEntry entry in transcript.Entries)
        {
            switch (entry.Kind)
            {
                case TranscriptEntryKind.UserMessage:
                case TranscriptEntryKind.AssistantMessage:
                case TranscriptEntryKind.Reasoning:
                case TranscriptEntryKind.ToolCall:
                    previousUuid = AppendTurnLine(lines, transcript, entry, previousUuid, ref first);
                    if (entry.Kind == TranscriptEntryKind.UserMessage && entry.Text is { Length: > 0 } prompt)
                    {
                        lastPrompt = prompt;
                    }

                    break;

                case TranscriptEntryKind.ToolResult:
                    previousUuid = AppendTurnLine(lines, transcript, entry, previousUuid, ref first);
                    break;

                case TranscriptEntryKind.System:
                    break;
            }
        }

        lines.Add(Serialize(new JsonObject
        {
            ["type"] = "last-prompt",
            ["lastPrompt"] = lastPrompt ?? transcript.Title ?? string.Empty,
            ["leafUuid"] = previousUuid,
            ["sessionId"] = ResolveId(transcript),
        }));

        return [new SessionOutputFile(FileNameFor(transcript), string.Join('\n', lines) + "\n")];
    }

    /// <summary>
    /// Appends the line one entry belongs on, returning the uuid that becomes the next
    /// entry's parent.
    /// </summary>
    /// <remarks>
    /// A tool call rides on an assistant line and a tool result on a user line, which
    /// is how the format carries them: the chain is the conversation, not the entries.
    /// </remarks>
    private static string AppendTurnLine(
        List<string> lines,
        SessionTranscript transcript,
        TranscriptEntry entry,
        string parentUuid,
        ref bool first)
    {
        string uuid = Guid.NewGuid().ToString();
        bool assistantLine = entry.Kind is not (TranscriptEntryKind.UserMessage or TranscriptEntryKind.ToolResult);

        var record = new JsonObject
        {
            ["parentUuid"] = first ? null : parentUuid,
            ["isSidechain"] = false,
            ["type"] = assistantLine ? "assistant" : "user",
            ["uuid"] = uuid,
            ["timestamp"] = entry.Timestamp.ToUniversalTime().ToString("o"),
            ["sessionId"] = ResolveId(transcript),
            ["message"] = new JsonObject
            {
                ["role"] = assistantLine ? "assistant" : "user",
                ["content"] = new JsonArray { ToContentBlock(entry) },
            },
        };

        if (transcript.WorkingDirectory is { Length: > 0 } cwd)
        {
            record["cwd"] = cwd;
        }

        if (first)
        {
            record["promptId"] = Guid.NewGuid().ToString();
        }

        lines.Add(Serialize(record));
        first = false;
        return uuid;
    }

    private static JsonObject ToContentBlock(TranscriptEntry entry) => entry.Kind switch
    {
        TranscriptEntryKind.AssistantMessage or TranscriptEntryKind.UserMessage =>
            new JsonObject { ["type"] = "text", ["text"] = entry.Text ?? string.Empty },
        TranscriptEntryKind.Reasoning =>
            new JsonObject { ["type"] = "thinking", ["thinking"] = entry.Text ?? string.Empty },
        TranscriptEntryKind.ToolCall => new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = entry.ToolCallId ?? Guid.NewGuid().ToString(),
            ["name"] = entry.ToolName ?? "unknown",
            ["input"] = ParseArguments(entry.ToolArguments),
        },
        TranscriptEntryKind.ToolResult => new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = entry.ToolCallId ?? Guid.NewGuid().ToString(),
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = entry.ToolResult ?? entry.Text ?? string.Empty },
            },
        },
        _ => new JsonObject { ["type"] = "text", ["text"] = entry.Text ?? string.Empty },
    };

    private static JsonNode ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(arguments) ?? new JsonObject();
        }
        catch (JsonException)
        {
            // An argument blob that is not JSON is still the argument blob; handing it
            // over as a string beats dropping what the model asked for.
            return JsonValue.Create(arguments)!;
        }
    }

    private static string? ReadToolResultText(JsonObject block)
    {
        JsonNode? content = block["content"];
        switch (content)
        {
            case JsonArray array:
                return string.Join(
                    '\n',
                    array
                        .OfType<JsonObject>()
                        .Select(item => ReadString(item, "text"))
                        .Where(text => !string.IsNullOrEmpty(text)));
            case JsonValue value when value.TryGetValue(out string? text):
                return text;
            default:
                return null;
        }
    }

    private static string ResolveId(SessionTranscript transcript) =>
        Guid.TryParse(transcript.Id, out Guid parsed) ? parsed.ToString() : Guid.NewGuid().ToString();

    private static IEnumerable<string> SplitLines(string payload) =>
        payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

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
        return ReadString(record, "timestamp") is { Length: > 0 } text
            && DateTimeOffset.TryParse(text, out timestamp);
    }

    private static string Serialize(JsonObject record) =>
        record.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
