using System.Collections.ObjectModel;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace RuAgent.Harness.Core.Context;

public sealed record ModelContextSnapshot
{
    public ModelContextSnapshot(
        IReadOnlyList<ModelContextEntry> entries,
        ReadOnlyMemory<byte> canonicalUtf8)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var copiedEntries = entries.ToArray();
        if (copiedEntries.Any(entry => entry is null))
        {
            throw new ArgumentException("Context entries cannot contain null.", nameof(entries));
        }

        Entries = new ReadOnlyCollection<ModelContextEntry>(copiedEntries);
        CanonicalUtf8 = canonicalUtf8.ToArray();
    }

    public IReadOnlyList<ModelContextEntry> Entries { get; }

    public ReadOnlyMemory<byte> CanonicalUtf8 { get; }

    internal static byte[] Canonicalize(IReadOnlyList<ModelContextEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions
                   {
                       Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                   }))
        {
            writer.WriteStartArray();
            foreach (var entry in entries)
            {
                WriteEntry(writer, entry);
            }

            writer.WriteEndArray();
        }

        return stream.ToArray();
    }

    private static void WriteEntry(Utf8JsonWriter writer, ModelContextEntry entry)
    {
        writer.WriteStartObject();
        writer.WriteString("source_id", entry.SourceId);
        writer.WriteNumber("sequence", entry.Sequence);
        writer.WriteString("role", RoleName(entry.Role));
        writer.WriteString("kind", KindName(entry.Kind));
        switch (entry.Kind)
        {
            case ModelContextEntryKind.Text:
                writer.WriteString("text", entry.Content);
                break;
            case ModelContextEntryKind.AssistantStep:
                WriteAssistantStep(writer, entry.AssistantStep!);
                break;
            case ModelContextEntryKind.ToolResult:
                WriteToolResult(writer, entry.ToolReceipt!, entry.Content!);
                break;
            case ModelContextEntryKind.Summary:
                writer.WriteString("compaction_id", entry.SourceId);
                writer.WriteString("text", entry.Content);
                writer.WritePropertyName("covered_ranges");
                writer.WriteStartArray();
                foreach (var range in entry.SummaryRanges!)
                {
                    writer.WriteStartObject();
                    writer.WriteString("source_id", range.SourceId);
                    writer.WriteNumber("start_sequence", range.StartSequence);
                    writer.WriteNumber("end_sequence", range.EndSequence);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entry));
        }

        writer.WriteEndObject();
    }

    private static void WriteAssistantStep(Utf8JsonWriter writer, AssistantStep step)
    {
        if (step.Text is null)
        {
            writer.WriteNull("text");
        }
        else
        {
            writer.WriteString("text", step.Text);
        }

        writer.WritePropertyName("tool_calls");
        writer.WriteStartArray();
        foreach (var call in step.ToolCalls)
        {
            writer.WriteStartObject();
            writer.WriteString("provider_call_id", call.ProviderCallId);
            writer.WriteString("tool_id", call.ToolId);
            writer.WriteString("tool_version", call.ToolVersion);
            writer.WritePropertyName("arguments");
            WriteCanonicalJson(writer, call.Arguments);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteString("finish_reason", FinishReasonName(step.FinishReason));
        writer.WriteNumber("estimated_input_tokens", step.EstimatedInputTokens);
        if (step.Usage is not null)
        {
            writer.WritePropertyName("usage");
            step.Usage.WriteTo(writer);
        }
    }

    private static void WriteToolResult(
        Utf8JsonWriter writer,
        ToolExecutionReceipt receipt,
        string content)
    {
        writer.WriteString("execution_id", receipt.ExecutionId.Value);
        writer.WriteString("tool_call_id", receipt.ToolCallId.Value);
        writer.WriteString("provider_call_id", receipt.ProviderCallId);
        writer.WriteString("outcome", OutcomeName(receipt.Outcome));
        writer.WriteString("code", receipt.Code);
        if (receipt.ResultPayloadId is { } payloadId)
        {
            writer.WriteString("result_payload_id", payloadId.Value);
        }
        else
        {
            writer.WriteNull("result_payload_id");
        }

        writer.WriteBoolean("side_effects_possible", receipt.SideEffectsPossible);
        writer.WriteString("content", content);
    }

    private static void WriteCanonicalJson(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(
                             property => property.Name,
                             StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalJson(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalJson(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                element.WriteTo(writer);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new ArgumentException("Unsupported JSON value kind.", nameof(element));
        }
    }

    private static string RoleName(ModelContextRole role) => role switch
    {
        ModelContextRole.System => "system",
        ModelContextRole.User => "user",
        ModelContextRole.Assistant => "assistant",
        ModelContextRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static string KindName(ModelContextEntryKind kind) => kind switch
    {
        ModelContextEntryKind.Text => "text",
        ModelContextEntryKind.AssistantStep => "assistant_step",
        ModelContextEntryKind.ToolResult => "tool_result",
        ModelContextEntryKind.Summary => "summary",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static string FinishReasonName(AssistantFinishReason reason) => reason switch
    {
        AssistantFinishReason.Stop => "stop",
        AssistantFinishReason.ToolCalls => "tool_calls",
        AssistantFinishReason.Length => "length",
        AssistantFinishReason.ContentFilter => "content_filter",
        AssistantFinishReason.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static string OutcomeName(ToolExecutionOutcome outcome) => outcome switch
    {
        ToolExecutionOutcome.Succeeded => "succeeded",
        ToolExecutionOutcome.Failed => "failed",
        ToolExecutionOutcome.TimedOut => "timed_out",
        ToolExecutionOutcome.Cancelled => "cancelled",
        ToolExecutionOutcome.OutcomeUnknown => "outcome_unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };
}
