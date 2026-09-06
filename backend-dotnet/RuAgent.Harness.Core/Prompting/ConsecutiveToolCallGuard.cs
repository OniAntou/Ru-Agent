using System.Text.Json;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Prompting;

public static class ConsecutiveToolCallGuard
{
    public static bool IsRepeated(
        ModelContextSnapshot context,
        IReadOnlyList<ModelToolCall> proposed)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(proposed);
        if (proposed.Count == 0 || context.Entries.Count < 2)
        {
            return false;
        }

        var index = context.Entries.Count - 1;
        var receiptCount = 0;
        while (index >= 0 && context.Entries[index].Kind == ModelContextEntryKind.ToolResult)
        {
            receiptCount++;
            index--;
        }

        if (receiptCount == 0
            || index < 0
            || context.Entries[index].Kind != ModelContextEntryKind.AssistantStep)
        {
            return false;
        }

        var prior = context.Entries[index].AssistantStep!.ToolCalls;
        if (prior.Count != proposed.Count || receiptCount != prior.Count)
        {
            return false;
        }

        for (var callIndex = 0; callIndex < prior.Count; callIndex++)
        {
            if (!SameCall(prior[callIndex], proposed[callIndex]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameCall(ModelToolCall left, ModelToolCall right) =>
        string.Equals(left.ToolId, right.ToolId, StringComparison.Ordinal)
        && string.Equals(left.ToolVersion, right.ToolVersion, StringComparison.Ordinal)
        && CanonicalJson(left.Arguments).AsSpan().SequenceEqual(CanonicalJson(right.Arguments));

    private static byte[] CanonicalJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteValue(writer, element);
        }

        return stream.ToArray();
    }

    private static void WriteValue(Utf8JsonWriter writer, JsonElement element)
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
                    WriteValue(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteValue(writer, item);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
