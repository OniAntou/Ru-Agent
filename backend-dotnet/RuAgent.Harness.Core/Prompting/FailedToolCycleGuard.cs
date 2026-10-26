using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Prompting;

internal static class FailedToolCycleGuard
{
    private const int MaximumContextEntries = 96;
    private const int MinimumPeriod = 2;
    private const int MaximumPeriod = 3;

    public static bool TryDescribe(
        ModelContextSnapshot context,
        IReadOnlyList<ModelToolCall> proposed,
        out string? receiptCode)
    {
        receiptCode = null;
        if (proposed.Count == 0)
        {
            return false;
        }

        var batches = ReadFailedBatchTail(context);
        for (var period = MinimumPeriod; period <= MaximumPeriod; period++)
        {
            if (!CompletesThirdTraversal(batches, proposed, period))
            {
                continue;
            }

            receiptCode = batches[^1].ReceiptCode;
            return true;
        }

        return false;
    }

    private static List<FailedBatch> ReadFailedBatchTail(ModelContextSnapshot context)
    {
        var entries = context.Entries;
        var start = Math.Max(0, entries.Count - MaximumContextEntries);
        var batches = new List<FailedBatch>();
        for (var index = start; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (IsGuardNote(entry))
            {
                continue;
            }

            if (entry.Kind != ModelContextEntryKind.AssistantStep)
            {
                batches.Clear();
                continue;
            }

            if (!TryReadBatch(entries, ref index, batches))
            {
                batches.Clear();
            }
        }

        return batches;
    }

    private static bool TryReadBatch(
        IReadOnlyList<ModelContextEntry> entries,
        ref int index,
        List<FailedBatch> batches)
    {
        var calls = entries[index].AssistantStep!.ToolCalls;
        if (calls.Count == 0)
        {
            return false;
        }

        var receiptCount = CountFollowingReceipts(entries, index, calls.Count);
        if (receiptCount != calls.Count)
        {
            return HasFollowingRepetitionNote(entries, index + 1);
        }

        var receipts = entries.Skip(index + 1).Take(receiptCount)
            .Select(result => result.ToolReceipt!)
            .ToArray();
        index += receiptCount;
        if (!IsFailedBatch(receipts))
        {
            batches.Clear();
            return true;
        }

        batches.Add(new FailedBatch(calls.ToArray(), FirstReceiptCode(receipts)));
        return true;
    }

    private static int CountFollowingReceipts(
        IReadOnlyList<ModelContextEntry> entries,
        int assistantIndex,
        int callCount)
    {
        var index = assistantIndex + 1;
        while (index < entries.Count
               && index - assistantIndex <= callCount
               && entries[index].Kind == ModelContextEntryKind.ToolResult)
        {
            index++;
        }

        return index - assistantIndex - 1;
    }

    private static bool CompletesThirdTraversal(
        IReadOnlyList<FailedBatch> batches,
        IReadOnlyList<ModelToolCall> proposed,
        int period)
    {
        var requiredHistory = period * 3 - 1;
        if (batches.Count < requiredHistory)
        {
            return false;
        }

        var firstIndex = batches.Count - requiredHistory;
        for (var index = 0; index < requiredHistory; index++)
        {
            if (!SameBatch(
                    batches[firstIndex + index].Calls,
                    batches[firstIndex + index % period].Calls))
            {
                return false;
            }
        }

        return SameBatch(proposed, batches[firstIndex + requiredHistory % period].Calls);
    }

    private static bool SameBatch(
        IReadOnlyList<ModelToolCall> left,
        IReadOnlyList<ModelToolCall> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (!ConsecutiveToolCallGuard.SameCall(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFailedBatch(IReadOnlyList<ToolExecutionReceipt> receipts) =>
        receipts.All(receipt => receipt.Outcome == ToolExecutionOutcome.Failed
            && !receipt.SideEffectsPossible
            && !ConsecutiveToolCallGuard.NeverExecuted(receipt));

    private static string? FirstReceiptCode(IReadOnlyList<ToolExecutionReceipt> receipts) =>
        receipts.Select(receipt => receipt.Code)
            .FirstOrDefault(code => !string.IsNullOrWhiteSpace(code));

    private static bool HasFollowingRepetitionNote(
        IReadOnlyList<ModelContextEntry> entries,
        int start)
    {
        var end = Math.Min(entries.Count, start + 3);
        for (var index = start; index < end; index++)
        {
            if (IsRepetitionNote(entries[index]))
            {
                return true;
            }

            if (entries[index].Kind != ModelContextEntryKind.Text)
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsGuardNote(ModelContextEntry entry) =>
        AgentRecoveryNotes.IsCommentaryProgress(entry)
        || entry.Kind == ModelContextEntryKind.Text
        && entry.Content is { } content
        && (content.StartsWith(ConsecutiveToolCallGuard.RepairNotePrefix, StringComparison.Ordinal)
            || content.StartsWith(AgentRecoveryNotes.NotePrefix, StringComparison.Ordinal));

    private static bool IsRepetitionNote(ModelContextEntry entry) =>
        entry.Kind == ModelContextEntryKind.Text
        && entry.Content is { } content
        && content.StartsWith(ConsecutiveToolCallGuard.RepairNotePrefix, StringComparison.Ordinal);

    private sealed record FailedBatch(IReadOnlyList<ModelToolCall> Calls, string? ReceiptCode);
}
