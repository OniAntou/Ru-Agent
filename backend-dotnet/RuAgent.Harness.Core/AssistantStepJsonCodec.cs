using System.Text.Json;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

public static class AssistantStepJsonCodec
{
    public static byte[] Serialize(AssistantStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
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
                if (!ProviderCallIdContract.IsValid(call.ProviderCallId))
                {
                    throw new ArgumentException("Provider call identifier is invalid.", nameof(step));
                }

                writer.WriteStartObject();
                writer.WriteString("provider_call_id", call.ProviderCallId);
                writer.WriteString("tool_id", call.ToolId);
                writer.WriteString("tool_version", call.ToolVersion);
                writer.WritePropertyName("arguments");
                call.Arguments.WriteTo(writer);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("finish_reason", FinishReason(step.FinishReason));
            writer.WriteNumber("estimated_input_tokens", step.EstimatedInputTokens);
            if (step.ResponsesMessages is { } messages)
            {
                writer.WritePropertyName("responses_messages");
                messages.WriteTo(writer);
            }
            if (step.Usage is not null)
            {
                writer.WritePropertyName("usage");
                step.Usage.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static AssistantStep Deserialize(ReadOnlyMemory<byte> utf8)
    {
        try
        {
            using var document = JsonDocument.Parse(utf8);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("assistant_step_json_invalid");
            }

            var calls = root.GetProperty("tool_calls")
                .EnumerateArray()
                .Select(call => new ModelToolCall(
                    call.GetProperty("provider_call_id").GetString()!,
                    call.GetProperty("tool_id").GetString()!,
                    call.GetProperty("tool_version").GetString()!,
                    call.GetProperty("arguments")))
                .ToArray();
            var providerCallIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls)
            {
                if (!providerCallIds.Add(call.ProviderCallId))
                {
                    throw new InvalidDataException("assistant_step_provider_call_id_duplicate");
                }
            }
            ModelUsage? usage = null;
            if (root.TryGetProperty("usage", out var usageElement))
            {
                usage = ModelUsage.Read(usageElement);
            }

            return new AssistantStep(
                root.TryGetProperty("text", out var text)
                    && text.ValueKind != JsonValueKind.Null
                        ? text.GetString()
                        : null,
                calls,
                ParseFinishReason(root.GetProperty("finish_reason").GetString()),
                root.GetProperty("estimated_input_tokens").GetInt32(),
                usage,
                root.TryGetProperty("responses_messages", out var messages) && messages.ValueKind != JsonValueKind.Null
                    ? ResponsesAssistantMessages.Read(messages) : null);
        }
        catch (Exception exception) when (exception is
            JsonException
            or InvalidDataException
            or InvalidOperationException
            or KeyNotFoundException
            or ArgumentException)
        {
            throw new InvalidDataException("assistant_step_json_invalid", exception);
        }
    }

    private static string FinishReason(AssistantFinishReason reason) => reason switch
    {
        AssistantFinishReason.Stop => "stop",
        AssistantFinishReason.ToolCalls => "tool_calls",
        AssistantFinishReason.Length => "length",
        AssistantFinishReason.ContentFilter => "content_filter",
        AssistantFinishReason.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static AssistantFinishReason ParseFinishReason(string? reason) => reason switch
    {
        "stop" => AssistantFinishReason.Stop,
        "tool_calls" => AssistantFinishReason.ToolCalls,
        "length" => AssistantFinishReason.Length,
        "content_filter" => AssistantFinishReason.ContentFilter,
        "error" => AssistantFinishReason.Error,
        _ => throw new InvalidDataException("assistant_step_finish_reason_invalid"),
    };
}
