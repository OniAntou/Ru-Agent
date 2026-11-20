using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;

namespace RuBackend.Harness.ModelRunner;

/// <summary>
/// Repairs a projected history whose stored tool batches cannot be paired: an
/// interrupted source can leave a call without its receipt, a provider can reuse a
/// call id across batches, and a receipt can be missing its provider id. A batch is
/// kept only when every call has a receipt in order; orphan receipts and unpaired
/// batches are dropped. Everything that is not tool-call structure is kept as-is.
/// </summary>
internal static class ModelHistoryRepair
{
    internal static IReadOnlyList<ModelContextEntry> Sanitize(IReadOnlyList<ModelContextEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var kept = new List<ModelContextEntry>(entries.Count);
        var providerCallIds = new HashSet<string>(StringComparer.Ordinal);
        var toolCallIds = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        while (index < entries.Count)
        {
            var entry = entries[index];
            if (entry is null)
            {
                index++;
                continue;
            }

            if (entry.Kind == ModelContextEntryKind.ToolResult)
            {
                // A receipt that was not consumed by its batch has no call to answer.
                index++;
                continue;
            }

            if (entry.Kind != ModelContextEntryKind.AssistantStep
                || entry.AssistantStep is not { ToolCalls.Count: > 0 } step)
            {
                kept.Add(entry);
                index++;
                continue;
            }

            var calls = step.ToolCalls;
            var batchValid = true;
            var batchProviderCallIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var call in calls)
            {
                if (call is null
                    || !IsValidProviderCallId(call.ProviderCallId)
                    || !batchProviderCallIds.Add(call.ProviderCallId)
                    || providerCallIds.Contains(call.ProviderCallId))
                {
                    batchValid = false;
                    break;
                }
            }

            var receipts = new List<ModelContextEntry>(calls.Count);
            var batchToolCallIds = new HashSet<string>(StringComparer.Ordinal);
            var scan = index + 1;
            if (batchValid)
            {
                foreach (var call in calls)
                {
                    if (scan >= entries.Count
                        || entries[scan].Kind != ModelContextEntryKind.ToolResult
                        || entries[scan].ToolReceipt is not { } receipt
                        || !string.Equals(receipt.ProviderCallId, call.ProviderCallId, StringComparison.Ordinal)
                        || !batchToolCallIds.Add(receipt.ToolCallId.Value)
                        || toolCallIds.Contains(receipt.ToolCallId.Value))
                    {
                        batchValid = false;
                        break;
                    }

                    receipts.Add(entries[scan]);
                    scan++;
                }
            }

            if (!batchValid)
            {
                // Drop the unpaired batch and any receipts that trailed it; a later valid
                // batch can still be carried.
                index++;
                while (index < entries.Count && entries[index].Kind == ModelContextEntryKind.ToolResult)
                {
                    index++;
                }

                continue;
            }

            kept.Add(entry);
            kept.AddRange(receipts);
            foreach (var call in calls)
            {
                providerCallIds.Add(call.ProviderCallId);
            }

            foreach (var toolCallId in batchToolCallIds)
            {
                toolCallIds.Add(toolCallId);
            }

            index = scan;
        }

        return kept;
    }

    private static bool IsValidProviderCallId(string? value) =>
        value is { Length: >= 1 and <= 256 }
        && value.Trim().Length == value.Length
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.');
}
