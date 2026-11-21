using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;

namespace RuBackend.Harness.ModelRunner;

/// <summary>
/// Repairs a projected history whose stored tool batches cannot be paired: an
/// interrupted source can leave a call without its receipt, a provider can reuse a
/// call id across batches, and a receipt can be missing its provider id. A batch is
/// kept only when every call has a receipt in order; orphan receipts and unpaired
/// batches are dropped. Everything that is not tool-call structure is kept as-is.
/// When two valid batches reuse a call id, the newer batch wins: it is the one the
/// model most recently acted on, and dropping it would make the model repeat work.
/// </summary>
internal static class ModelHistoryRepair
{
    internal sealed record Result(IReadOnlyList<ModelContextEntry> Entries, bool DroppedAny);

    internal static Result Sanitize(IReadOnlyList<ModelContextEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var chunks = new List<Chunk>(entries.Count);
        var dropped = false;
        var index = 0;
        while (index < entries.Count)
        {
            var entry = entries[index];
            if (entry is null)
            {
                index++;
                dropped = true;
                continue;
            }

            if (entry.Kind == ModelContextEntryKind.ToolResult)
            {
                // A receipt that was not consumed by its batch has no call to answer.
                index++;
                dropped = true;
                continue;
            }

            if (entry.Kind != ModelContextEntryKind.AssistantStep
                || entry.AssistantStep is not { ToolCalls.Count: > 0 } step)
            {
                chunks.Add(new Chunk(entry, [], isBatch: false));
                index++;
                continue;
            }

            var calls = step.ToolCalls;
            var batchProviderCallIds = new HashSet<string>(StringComparer.Ordinal);
            var batchValid = true;
            foreach (var call in calls)
            {
                if (call is null
                    || !IsValidProviderCallId(call.ProviderCallId)
                    || !batchProviderCallIds.Add(call.ProviderCallId))
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
                        || !batchToolCallIds.Add(receipt.ToolCallId.Value))
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
                dropped = true;
                index++;
                while (index < entries.Count && entries[index].Kind == ModelContextEntryKind.ToolResult)
                {
                    index++;
                }

                continue;
            }

            chunks.Add(new Chunk(entry, receipts, isBatch: true,
                ProviderCallIds: batchProviderCallIds, ToolCallIds: batchToolCallIds));
            index = scan;
        }

        var providerOwners = new Dictionary<string, Chunk>(StringComparer.Ordinal);
        var toolCallOwners = new Dictionary<string, Chunk>(StringComparer.Ordinal);
        foreach (var chunk in chunks)
        {
            if (!chunk.IsBatch)
            {
                continue;
            }

            var superseded = new HashSet<Chunk>();
            foreach (var providerCallId in chunk.ProviderCallIds)
            {
                if (providerOwners.TryGetValue(providerCallId, out var earlier))
                {
                    superseded.Add(earlier);
                }
            }

            foreach (var toolCallId in chunk.ToolCallIds)
            {
                if (toolCallOwners.TryGetValue(toolCallId, out var earlier))
                {
                    superseded.Add(earlier);
                }
            }

            foreach (var earlier in superseded)
            {
                // The newer batch is the one the model most recently acted on. Keep it
                // and release the older batch's ids so a still later batch may use them.
                earlier.Keep = false;
                dropped = true;
                foreach (var providerCallId in earlier.ProviderCallIds)
                {
                    if (providerOwners.TryGetValue(providerCallId, out var owner) && ReferenceEquals(owner, earlier))
                    {
                        providerOwners.Remove(providerCallId);
                    }
                }

                foreach (var toolCallId in earlier.ToolCallIds)
                {
                    if (toolCallOwners.TryGetValue(toolCallId, out var owner) && ReferenceEquals(owner, earlier))
                    {
                        toolCallOwners.Remove(toolCallId);
                    }
                }
            }

            foreach (var providerCallId in chunk.ProviderCallIds)
            {
                providerOwners[providerCallId] = chunk;
            }

            foreach (var toolCallId in chunk.ToolCallIds)
            {
                toolCallOwners[toolCallId] = chunk;
            }
        }

        var kept = new List<ModelContextEntry>(entries.Count);
        foreach (var chunk in chunks)
        {
            if (!chunk.Keep)
            {
                continue;
            }

            kept.Add(chunk.Entry);
            kept.AddRange(chunk.Receipts);
        }

        return new Result(kept, dropped);
    }

    private static bool IsValidProviderCallId(string? value) =>
        value is { Length: >= 1 and <= 256 }
        && value.Trim().Length == value.Length
        && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.');

    private sealed class Chunk(
        ModelContextEntry entry,
        IReadOnlyList<ModelContextEntry> receipts,
        bool isBatch,
        IReadOnlySet<string>? ProviderCallIds = null,
        IReadOnlySet<string>? ToolCallIds = null)
    {
        internal ModelContextEntry Entry { get; } = entry;
        internal IReadOnlyList<ModelContextEntry> Receipts { get; } = receipts;
        internal bool IsBatch { get; } = isBatch;
        internal IReadOnlySet<string> ProviderCallIds { get; } = ProviderCallIds ?? new HashSet<string>();
        internal IReadOnlySet<string> ToolCallIds { get; } = ToolCallIds ?? new HashSet<string>();
        // A repaired chunk is kept unless a newer batch reuses one of its call ids.
        internal bool Keep { get; set; } = true;
    }
}
