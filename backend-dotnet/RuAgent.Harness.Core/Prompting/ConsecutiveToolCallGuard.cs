using System.Text.Json;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Prompting;

public enum RepetitionGuardVerdict
{
    NotRepeated,
    Advise,
    Stop,
}

public static class ConsecutiveToolCallGuard
{
    // Model-visible marker for the corrective note. The guard also uses it to
    // count how many notes the run already carries, so the context stays the
    // single source of truth for "the model was already told".
    public const string RepairNotePrefix = "[REPETITION GUARD]";

    // Bounded self-repair budget: a model that ignores the note still stops,
    // but a weak model gets a real chance to change its arguments first.
    public const int MaximumRepairNotes = 2;


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

    /// <summary>
    /// Classifies the proposed calls against the previous assistant step: an
    /// identical repeat is either answered with a corrective note while the
    /// repair budget lasts, or stopped.
    /// </summary>
    public static RepetitionGuardVerdict Inspect(
        ModelContextSnapshot context,
        IReadOnlyList<ModelToolCall> proposed,
        out string? receiptCode)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(proposed);
        if (!TryDescribeRepetition(context, proposed, out receiptCode)
            && !FailedToolCycleGuard.TryDescribe(context, proposed, out receiptCode))
        {
            receiptCode = null;
            return RepetitionGuardVerdict.NotRepeated;
        }

        return RepairNoteCount(context) < MaximumRepairNotes
            ? RepetitionGuardVerdict.Advise
            : RepetitionGuardVerdict.Stop;
    }

    public static bool IsRepeated(
        ModelContextSnapshot context,
        IReadOnlyList<ModelToolCall> proposed) =>
        Inspect(context, proposed, out _) != RepetitionGuardVerdict.NotRepeated;

    /// <summary>
    /// Bounded corrective copy for the model. It names the rejection code only;
    /// arguments and file content never enter the note.
    /// </summary>
    public static string BuildRepairNote(string? receiptCode)
    {
        var code = string.IsNullOrWhiteSpace(receiptCode) ? "rejected" : receiptCode;
        return $"{RepairNotePrefix} You already sent this exact tool call and it did not "
            + $"succeed ({code}). Repeating the identical call cannot succeed. Change the "
            + "arguments, use a different tool, or explain what is blocking you.";
    }

    private static bool TryDescribeRepetition(
        ModelContextSnapshot context,
        IReadOnlyList<ModelToolCall> proposed,
        out string? receiptCode)
    {
        receiptCode = null;
        if (proposed.Count == 0 || context.Entries.Count < 2)
        {
            return false;
        }

        var index = context.Entries.Count - 1;
        // Advice replaces a suppressed call in model context. Look through our
        // trailing advice to the original result, without crossing a user turn.
        while (index >= 0
            && context.Entries[index].Kind == ModelContextEntryKind.Text
            && context.Entries[index].Role == ModelContextRole.System
            && context.Entries[index].Content?.StartsWith(RepairNotePrefix, StringComparison.Ordinal) == true)
        {
            index--;
        }
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

        receiptCode = receipts
            .Select(receipt => receipt?.Code)
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));
        return true;
    }

    private static int RepairNoteCount(ModelContextSnapshot context)
    {
        var count = 0;
        foreach (var entry in context.Entries)
        {
            if (entry.Kind == ModelContextEntryKind.Text
                && entry.Content is { } content
                && content.StartsWith(RepairNotePrefix, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
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

    internal static bool NeverExecuted(ToolExecutionReceipt receipt) =>
        receipt is not null && NeverExecutedCodes.Contains(receipt.Code);

    internal static bool SameCall(ModelToolCall left, ModelToolCall right) =>
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
