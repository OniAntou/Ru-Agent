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
                var readReceipts = await ExecuteReadsAsync(batch, execute, cancellationToken).ConfigureAwait(false);
                receipts.AddRange(readReceipts);
                if (readReceipts.Any(receipt => receipt.Outcome == ToolExecutionOutcome.OutcomeUnknown))
                    return new ReadOnlyCollection<ToolExecutionReceipt>(receipts);

                index = end;
                continue;
            }

            var call = ordered[index];
            using var timing = Diagnostics.HarnessPerformance.Measure(Diagnostics.HarnessPhase.ToolExecution);
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

    private static async Task<IReadOnlyList<ToolExecutionReceipt>> ExecuteReadsAsync(
        PreparedToolExecution[] calls,
        Func<PreparedToolExecution, CancellationToken, Task<ToolExecutionReceipt>> execute,
        CancellationToken cancellationToken)
    {
        var running = new Dictionary<int, Task<ToolExecutionReceipt>>();
        var completed = new ToolExecutionReceipt?[calls.Length];
        int next = 0;
        bool unknown = false;
        async Task<ToolExecutionReceipt> RunAsync(PreparedToolExecution call)
        {
            using var timing = Diagnostics.HarnessPerformance.Measure(Diagnostics.HarnessPhase.ToolExecution);
            return ValidateReceipt(call, await execute(call, cancellationToken).ConfigureAwait(false));
        }

        try
        {
            while (running.Count > 0 || (!unknown && next < calls.Length))
            {
                // Observe every currently settled call before admitting more work.
                // Unknown outcomes stop new starts; every already-started read still drains.
                foreach (var pair in running.Where(pair => pair.Value.IsCompleted).ToArray())
                {
                    var receipt = await pair.Value.ConfigureAwait(false);
                    completed[pair.Key] = receipt;
                    unknown |= receipt.Outcome == ToolExecutionOutcome.OutcomeUnknown;
                    running.Remove(pair.Key);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (!unknown && next < calls.Length && running.Count < MaxReadConcurrency)
                {
                    running.Add(next, RunAsync(calls[next]));
                    next++;
                    continue;
                }
                if (running.Count > 0) await Task.WhenAny(running.Values).ConfigureAwait(false);
            }
        }
        finally
        {
            // Returning/throwing before owned executions settle lets later runs
            // race their cleanup. Observe all failures without replacing the first.
            try { await Task.WhenAll(running.Values).ConfigureAwait(false); }
            catch { }
        }
        return completed.Take(next).Select(receipt => receipt!).ToArray();
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
