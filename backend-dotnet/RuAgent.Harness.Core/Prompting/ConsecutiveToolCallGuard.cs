using System.Text.Json;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Prompting;

public static class ConsecutiveToolCallGuard
{
    // Receipt codes where the tool never executed for infrastructure reasons.
    // Retrying the identical call once after one of these is self-correction,
    // not a loop. Policy denials and tool-level errors stay guarded: blindly
    // retrying those is the loop this guard exists to stop.
    private static readonly HashSet<string> NeverExecutedCodes = new(StringComparer.Ordinal)
    {
        "decision_stale",
        "transport_failure",
        "not_executed_after_unknown",
    };

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
        var receipts = new List<ToolExecutionReceipt>();
        while (index >= 0 && context.Entries[index].Kind == ModelContextEntryKind.ToolResult)
        {
            receipts.Add(context.Entries[index].ToolReceipt!);
            index--;
        }

        if (receipts.Count == 0
            || index < 0
            || context.Entries[index].Kind != ModelContextEntryKind.AssistantStep)
        {
            return false;
        }

        var prior = context.Entries[index].AssistantStep!.ToolCalls;
        if (prior.Count != proposed.Count || receipts.Count != prior.Count)
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

        // A single retry after an infrastructure failure that never executed
        // the tool is allowed through; a second consecutive identical
        // infra-failure still fails (bounded further by the step/call
        // budgets as a backstop).
        if (receipts.All(NeverExecuted) && !PrecededBySameInfraFailure(context, index, proposed))
        {
            return false;
        }

        return true;
    }

    private static bool PrecededBySameInfraFailure(
        ModelContextSnapshot context,
        int assistantIndex,
        IReadOnlyList<ModelToolCall> proposed)
    {
        var index = assistantIndex - 1;
        var receipts = new List<ToolExecutionReceipt>();
        while (index >= 0 && context.Entries[index].Kind == ModelContextEntryKind.ToolResult)
        {
            receipts.Add(context.Entries[index].ToolReceipt!);
            index--;
        }

        if (index < 0 || context.Entries[index].Kind != ModelContextEntryKind.AssistantStep)
        {
            return false;
        }

        var prior = context.Entries[index].AssistantStep!.ToolCalls;
        if (prior.Count != proposed.Count || receipts.Count != prior.Count)
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

        return receipts.All(NeverExecuted);
    }

    private static bool NeverExecuted(ToolExecutionReceipt receipt) =>
        receipt is not null && NeverExecutedCodes.Contains(receipt.Code);

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
