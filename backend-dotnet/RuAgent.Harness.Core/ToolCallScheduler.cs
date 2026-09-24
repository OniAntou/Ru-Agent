using System.Collections.ObjectModel;

namespace RuAgent.Harness.Core;

public sealed class ToolCallScheduler
{
    public const int MaxReadConcurrency = 8;

    public async Task<IReadOnlyList<ToolExecutionReceipt>> ExecuteAsync(
        IReadOnlyList<PreparedToolExecution> calls,
        Func<PreparedToolExecution, CancellationToken, Task<ToolExecutionReceipt>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calls);
        ArgumentNullException.ThrowIfNull(execute);
        var copied = calls.ToArray();
        if (copied.Any(call => call is null))
        {
            throw new ArgumentException("scheduled_tool_call_null");
        }

        var ordered = copied.OrderBy(call => call.ModelOrder).ToArray();

        if (ordered.Select(call => call.ModelOrder).Distinct().Count() != ordered.Length)
        {
            throw new ArgumentException("duplicate_tool_model_order");
        }

        if (ordered.Select(call => call.ToolCallId).Distinct().Count() != ordered.Length
            || ordered.Select(call => call.ExecutionId).Distinct().Count() != ordered.Length)
        {
            throw new ArgumentException("duplicate_tool_identity");
        }

        var receipts = new List<ToolExecutionReceipt>(ordered.Length);
        var index = 0;
        while (index < ordered.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ordered[index].AccessKind == ToolAccessKind.ReadOnly)
            {
                var end = index + 1;
                while (end < ordered.Length
                       && ordered[end].AccessKind == ToolAccessKind.ReadOnly)
                {
                    end++;
                }

                var batch = ordered[index..end];
                for (var batchIndex = 0; batchIndex < batch.Length; batchIndex += MaxReadConcurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var chunk = batch
                        .Skip(batchIndex)
                        .Take(MaxReadConcurrency)
                        .ToArray();
                    var tasks = chunk
                        .Select(call => execute(call, cancellationToken))
                        .ToArray();
                    var chunkReceipts = await Task.WhenAll(tasks).ConfigureAwait(false);
                    for (var chunkIndex = 0; chunkIndex < chunk.Length; chunkIndex++)
                    {
                        receipts.Add(ValidateReceipt(chunk[chunkIndex], chunkReceipts[chunkIndex]));
                    }

                    if (chunkReceipts.Any(receipt => receipt.Outcome == ToolExecutionOutcome.OutcomeUnknown))
                    {
                        return new ReadOnlyCollection<ToolExecutionReceipt>(receipts);
                    }
                }

                index = end;
                continue;
            }

            var call = ordered[index];
            var receipt = await execute(call, cancellationToken).ConfigureAwait(false);
            var validatedReceipt = ValidateReceipt(call, receipt);
            receipts.Add(validatedReceipt);
            index++;
            if (validatedReceipt.Outcome == ToolExecutionOutcome.OutcomeUnknown)
            {
                break;
            }
        }

        return new ReadOnlyCollection<ToolExecutionReceipt>(receipts);
    }

    private static ToolExecutionReceipt ValidateReceipt(
        PreparedToolExecution call,
        ToolExecutionReceipt? receipt)
    {
        if (receipt is null
            || receipt.ExecutionId != call.ExecutionId
            || receipt.ToolCallId != call.ToolCallId)
        {
            throw new InvalidOperationException("tool_receipt_identity_mismatch");
        }

        return receipt;
    }
}
