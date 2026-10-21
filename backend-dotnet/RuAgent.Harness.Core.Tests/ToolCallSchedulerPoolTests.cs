using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ToolCallSchedulerPoolTests
{
    [Fact]
    public async Task An_observed_unknown_outcome_stops_even_the_next_read()
    {
        var starts = new List<int>();
        var receipts = await new ToolCallScheduler().ExecuteAsync([Call(1), Call(2), Call(3, ToolAccessKind.Mutation)],
            (call, _) =>
            {
                starts.Add(call.ModelOrder);
                return Task.FromResult(new ToolExecutionReceipt(call.ExecutionId, call.ToolCallId,
                    ToolExecutionOutcome.OutcomeUnknown, "transport_failure", null, false));
            }, default);
        Assert.Equal([1], starts);
        Assert.Single(receipts);
    }

    [Fact]
    public async Task Cancellation_drains_owned_reads_before_returning_and_does_not_fill_more_slots()
    {
        using var cancellation = new CancellationTokenSource();
        var allStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int starts = 0;
        var run = new ToolCallScheduler().ExecuteAsync(Enumerable.Range(1, 9).Select(order => Call(order)).ToArray(),
            async (call, _) =>
            {
                if (Interlocked.Increment(ref starts) == ToolCallScheduler.MaxReadConcurrency) allStarted.TrySetResult();
                // Deliberately model a tool which owns asynchronous cancellation cleanup.
                await release.Task;
                return Receipt(call);
            }, cancellation.Token);
        try
        {
            await allStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            Assert.False(run.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(ToolCallScheduler.MaxReadConcurrency, starts);
    }

    [Fact]
    public async Task A_free_slot_starts_the_next_read_without_waiting_for_the_slowest_read()
    {
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ninth = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = Enumerable.Range(1, 9).Select(order => Call(order)).ToArray();
        var run = new ToolCallScheduler().ExecuteAsync(calls, async (call, token) =>
        {
            if (call.ModelOrder == 1) await slow.Task.WaitAsync(token);
            if (call.ModelOrder == 9) ninth.TrySetResult();
            return Receipt(call);
        }, default);
        try
        {
            await ninth.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(run.IsCompleted);
        }
        finally { slow.TrySetResult(); await run; }
        Assert.Equal(calls.Select(call => call.ToolCallId), (await run).Select(receipt => receipt.ToolCallId));
    }

    [Fact]
    public async Task Failure_drains_started_calls_and_never_crosses_the_mutation_barrier()
    {
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new[] { Call(1), Call(2), Call(3, ToolAccessKind.Mutation) };
        var starts = new List<int>();
        var run = new ToolCallScheduler().ExecuteAsync(calls, async (call, token) =>
        {
            starts.Add(call.ModelOrder);
            if (call.ModelOrder == 1) await slow.Task.WaitAsync(token);
            else { failed.TrySetResult(); throw new InvalidOperationException("synthetic_failure"); }
            return Receipt(call);
        }, default);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(run.IsCompleted);
        slow.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Equal([1, 2], starts);
    }

    private static PreparedToolExecution Call(int order, ToolAccessKind access = ToolAccessKind.ReadOnly) => new(
        new ExecutionId("exec_" + order.ToString("x32")), new ToolCallId("call_" + order.ToString("x32")),
        order, access, "synthetic", []);
    private static ToolExecutionReceipt Receipt(PreparedToolExecution call) => new(
        call.ExecutionId, call.ToolCallId, ToolExecutionOutcome.Succeeded, "ok", null, false);
}
