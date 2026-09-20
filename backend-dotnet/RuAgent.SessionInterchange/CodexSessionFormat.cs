using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.SessionInterchange;

/// <summary>
/// Codex's rollout format: one JSONL file per thread at
/// <c>~/.codex/sessions/&lt;yyyy&gt;/&lt;MM&gt;/&lt;dd&gt;/rollout-&lt;timestamp&gt;-&lt;threadId&gt;.jsonl</c>,
/// every line stamped with a monotonic <c>ordinal</c>, plus a line in
/// <c>~/.codex/session_index.jsonl</c> that is what the thread picker reads.
/// </summary>
/// <remarks>
/// Ordering here is positional, not linked: there is no <c>parentUuid</c> to get wrong,
/// but there is also nothing to repair a line that lands out of order, so the writer
/// emits every line with an ordinal and a timestamp in the sequence it was given.
///
/// Tool calls are written as <c>function_call</c>/<c>function_call_output</c> rather
/// than the newer <c>custom_tool_call</c>: the newer shape carries a JavaScript body
/// that only Codex's own runtime can execute, while our arguments are already JSON,
/// which is exactly what the function-call shape takes.
///
/// The three fields a real <c>session_meta</c> carries that describe the install that
/// wrote it (<c>originator</c>, <c>cli_version</c>, <c>base_instructions</c>) are left
/// out rather than copied from somewhere else: padding them with this build's values
/// would tell the partner a story about itself that is not true.
/// </remarks>
public sealed class CodexSessionFormat : ISessionFormat
{
    /// <summary>Name of the sibling file the thread picker reads.</summary>
    public const string IndexFileName = "session_index.jsonl";

    /// <inheritdoc />
    public string Id => "codex";

    /// <inheritdoc />
    public string DisplayName => "Codex";

    /// <inheritdoc />
    public bool CanWrite => true;

    /// <inheritdoc />
    public string WriteRefusal => string.Empty;

    /// <summary>The rollout file name Codex expects, keyed by date like the ones it writes.</summary>
    public static string FileNameFor(SessionTranscript transcript)
    {
        DateTimeOffset stamp = transcript.CreatedAt == default ? DateTimeOffset.UtcNow : transcript.CreatedAt;
        return $"rollout-{stamp.ToUniversalTime():yyyy-MM-dd'T'HH-mm-ss}-{ResolveId(transcript)}.jsonl";
    }

    /// <inheritdoc />
    public SessionTranscript Read(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var entries = new List<TranscriptEntry>();
        string? sessionId = null;
        string? workingDirectory = null;
        DateTimeOffset createdAt = default;
        bool sawMeta = false;
        bool sawResponseItem = false;

        foreach (string line in payload.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonNode? node = TryParse(line);
            if (node is not JsonObject record || record["payload"] is not JsonObject item)
            {
                continue;
            }

            string? type = ReadString(record, "type");
            DateTimeOffset timestamp = TryReadTimestamp(record, out DateTimeOffset stamped) ? stamped : createdAt;

            if (type == "session_meta")
            {
                sawMeta = true;
                sessionId ??= ReadString(item, "session_id") ?? ReadString(item, "id");
                workingDirectory ??= ReadString(item, "cwd");
                if (createdAt == default && TryReadTimestamp(item, out DateTimeOffset created))
                {
                    createdAt = created;
                }

                continue;
            }

            if (type != "response_item")
            {
                continue;
            }

            sawResponseItem = true;
            string? itemType = ReadString(item, "type");
            switch (itemType)
            {
                case "message":
                    entries.AddRange(ReadMessage(item, timestamp));
                    break;

                case "reasoning" when ReadSummary(item) is { Length: > 0 } reasoning:
                    entries.Add(new TranscriptEntry(TranscriptEntryKind.Reasoning, timestamp, Text: reasoning));
                    break;

                case "custom_tool_call" or "function_call":
                    entries.Add(new TranscriptEntry(
                        TranscriptEntryKind.ToolCall,
                        timestamp,
                        ToolName: ReadString(item, "name"),
                        ToolCallId: ReadString(item, "call_id"),
                        ToolArguments: ReadString(item, "arguments") ?? ReadString(item, "input")));
                    break;

                case "custom_tool_call_output" or "function_call_output":
                    entries.Add(new TranscriptEntry(
                        TranscriptEntryKind.ToolResult,
                        timestamp,
                        ToolCallId: ReadString(item, "call_id"),
                        ToolResult: ReadToolOutput(item)));
                    break;
            }
        }

        if (!sawMeta && !sawResponseItem)
        {
            throw new InvalidDataException(
                "This payload has neither a session_meta line nor a response_item line, so it is not a Codex rollout.");
        }

        if (entries.Count == 0)
        {
            throw new InvalidDataException("This Codex rollout has no readable turns.");
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

        string id = ResolveId(transcript);
        string turnId = NewUuidV7();
        DateTimeOffset started = transcript.Entries.Count > 0 ? transcript.Entries[0].Timestamp : DateTimeOffset.UtcNow;
        DateTimeOffset completed = transcript.Entries.Count > 0 ? transcript.Entries[^1].Timestamp : started;

        var lines = new List<string>
        {
            Serialize(Line(0, "session_meta", MetaPayload(transcript, id), started)),
        };
        int ordinal = 1;

        lines.Add(Serialize(Line(ordinal++, "event_msg", new JsonObject
        {
            ["type"] = "task_started",
            ["turn_id"] = turnId,
            ["started_at"] = started.ToUnixTimeSeconds(),
        }, started)));

        string? lastAssistantText = null;
        foreach (TranscriptEntry entry in transcript.Entries)
        {
            JsonObject? payload = ResponseItemFor(entry, turnId);
            if (payload is null)
            {
                continue;
            }

            if (entry.Kind == TranscriptEntryKind.AssistantMessage && entry.Text is { Length: > 0 } text)
            {
                lastAssistantText = text;
            }

            lines.Add(Serialize(Line(ordinal++, "response_item", payload, entry.Timestamp)));
        }

        lines.Add(Serialize(Line(ordinal, "event_msg", new JsonObject
        {
            ["type"] = "task_complete",
            ["turn_id"] = turnId,
            ["last_agent_message"] = lastAssistantText ?? string.Empty,
            ["started_at"] = started.ToUnixTimeSeconds(),
            ["completed_at"] = completed.ToUnixTimeSeconds(),
        }, completed)));

        var index = new JsonObject
        {
            ["id"] = id,
            ["thread_name"] = transcript.Title ?? $"Imported session {started:yyyy-MM-dd HH:mm}",
            ["updated_at"] = completed.ToUniversalTime().ToString("o"),
        };

        return
        [
            new SessionOutputFile(FileNameFor(transcript), string.Join('\n', lines) + "\n"),
            new SessionOutputFile(IndexFileName, Serialize(index) + "\n"),
        ];
    }

    private static JsonObject MetaPayload(SessionTranscript transcript, string id)
    {
        var payload = new JsonObject
        {
            ["session_id"] = id,
            ["id"] = id,
            ["timestamp"] = (transcript.CreatedAt == default ? DateTimeOffset.UtcNow : transcript.CreatedAt)
                .ToUniversalTime()
                .ToString("o"),
            ["thread_source"] = "user",
        };

        if (transcript.WorkingDirectory is { Length: > 0 } cwd)
        {
            payload["cwd"] = cwd;
        }

        return payload;
    }

    private static JsonObject? ResponseItemFor(TranscriptEntry entry, string turnId) => entry.Kind switch
    {
        TranscriptEntryKind.UserMessage => new JsonObject
        {
            ["type"] = "message",
            ["id"] = "msg_" + NewUuidV7(),
            ["role"] = "user",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = entry.Text ?? string.Empty } },
            ["internal_chat_message_metadata_passthrough"] = new JsonObject { ["turn_id"] = turnId },
        },
        TranscriptEntryKind.AssistantMessage => new JsonObject
        {
            ["type"] = "message",
            ["id"] = "msg_" + NewUuidV7(),
            ["role"] = "assistant",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "output_text", ["text"] = entry.Text ?? string.Empty } },
            ["internal_chat_message_metadata_passthrough"] = new JsonObject { ["turn_id"] = turnId },
        },
        TranscriptEntryKind.Reasoning => new JsonObject
        {
            ["type"] = "reasoning",
            ["id"] = "rs_" + NewUuidV7(),
            ["summary"] = new JsonArray { new JsonObject { ["type"] = "summary_text", ["text"] = entry.Text ?? string.Empty } },
            ["content"] = null,
            ["internal_chat_message_metadata_passthrough"] = new JsonObject { ["turn_id"] = turnId },
        },
        TranscriptEntryKind.ToolCall => new JsonObject
        {
            ["type"] = "function_call",
            ["id"] = "fc_" + NewUuidV7(),
            ["name"] = entry.ToolName ?? "unknown",
            ["arguments"] = entry.ToolArguments ?? "{}",
            ["call_id"] = entry.ToolCallId ?? "call_" + NewUuidV7(),
            ["internal_chat_message_metadata_passthrough"] = new JsonObject { ["turn_id"] = turnId },
        },
        TranscriptEntryKind.ToolResult => new JsonObject
        {
            ["type"] = "function_call_output",
            ["id"] = "fco_" + NewUuidV7(),
            ["call_id"] = entry.ToolCallId ?? "call_" + NewUuidV7(),
            ["output"] = entry.ToolResult ?? entry.Text ?? string.Empty,
        },
        TranscriptEntryKind.System => new JsonObject
        {
            ["type"] = "message",
            ["id"] = "msg_" + NewUuidV7(),
            ["role"] = "developer",
            ["content"] = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = entry.Text ?? string.Empty } },
        },
        _ => null,
    };

    private static IEnumerable<TranscriptEntry> ReadMessage(JsonObject item, DateTimeOffset timestamp)
    {
        TranscriptEntryKind kind = ReadString(item, "role") switch
        {
            "user" => TranscriptEntryKind.UserMessage,
            "assistant" => TranscriptEntryKind.AssistantMessage,
            _ => TranscriptEntryKind.System,
        };

        if (item["content"] is not JsonArray content)
        {
            yield break;
        }

        foreach (JsonObject block in content.OfType<JsonObject>())
        {
            if (ReadString(block, "text") is { Length: > 0 } text)
            {
                yield return new TranscriptEntry(kind, timestamp, Text: text);
            }
        }
    }

    private static string ReadSummary(JsonObject item)
    {
        if (item["summary"] is not JsonArray summary)
        {
            return string.Empty;
        }

        return string.Join(
            '\n',
            summary
                .OfType<JsonObject>()
                .Select(block => ReadString(block, "text"))
                .Where(text => !string.IsNullOrEmpty(text)));
    }

    private static string? ReadToolOutput(JsonObject item)
    {
        JsonNode? output = item["output"];
        return output switch
        {
            JsonArray array => string.Join(
                '\n',
                array
                    .OfType<JsonObject>()
                    .Select(block => ReadString(block, "text"))
                    .Where(text => !string.IsNullOrEmpty(text))),
            JsonValue value when value.TryGetValue(out string? text) => text,
            _ => null,
        };
    }

    private static JsonObject Line(int ordinal, string type, JsonObject payload, DateTimeOffset timestamp) => new()
    {
        ["timestamp"] = timestamp.ToUniversalTime().ToString("o"),
        ["ordinal"] = ordinal,
        ["type"] = type,
        ["payload"] = payload,
    };

    /// <summary>
    /// A UUIDv7, which is the version Codex mints its own ids in: the first 48 bits are
    /// the millisecond timestamp, so ids sort in the order the sessions happened.
    /// </summary>
    internal static string NewUuidV7()
    {
        Span<byte> bytes = stackalloc byte[16];
        Random.Shared.NextBytes(bytes);

        long unixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMilliseconds >> 40);
        bytes[1] = (byte)(unixMilliseconds >> 32);
        bytes[2] = (byte)(unixMilliseconds >> 24);
        bytes[3] = (byte)(unixMilliseconds >> 16);
        bytes[4] = (byte)(unixMilliseconds >> 8);
        bytes[5] = (byte)unixMilliseconds;
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        return string.Create(36, bytes.ToArray(), static (span, value) =>
        {
            const string hex = "0123456789abcdef";
            int position = 0;
            for (int index = 0; index < value.Length; index++)
            {
                if (index is 4 or 6 or 8 or 10)
                {
                    span[position++] = '-';
                }

                span[position++] = hex[value[index] >> 4];
                span[position++] = hex[value[index] & 0x0F];
            }
        });
    }

    private static string ResolveId(SessionTranscript transcript)
    {
        if (Guid.TryParse(transcript.Id, out Guid parsed))
        {
            return parsed.ToString();
        }

        return NewUuidV7();
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
        return ReadString(record, "timestamp") is { Length: > 0 } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);
    }

    private static string Serialize(JsonObject record) =>
        record.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
}
