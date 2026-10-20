using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatAssistantPayloadReaderTests
{
    [Fact]
    public async Task Reads_four_at_a_time_and_keeps_original_order_and_payload_kinds()
    {
        var firstWave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = Enumerable.Range(0, 8).Select(_ => new TaskCompletionSource<AgentPayloadContent>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var calls = 0;
        var active = 0;
        var peak = 0;
        var resultTask = ChatAssistantPayloadReader.ReadAsync(Enumerable.Range(0, 8).Select(i => i.ToString()).ToArray(), async (id, ct) =>
        {
            var i = int.Parse(id);
            var current = Interlocked.Increment(ref active);
            peak = Math.Max(peak, current);
            if (Interlocked.Increment(ref calls) == 4) firstWave.TrySetResult();
            try { return await release[i].Task.WaitAsync(ct); }
            finally { Interlocked.Decrement(ref active); }
        }, default);

        await firstWave.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, calls);
        Assert.False(resultTask.IsCompleted);
        // Complete the requests out of order; the reconstructed reply must not follow network order.
        for (var i = 7; i >= 0; i--) release[i].SetResult(new(i % 2 == 0 ? "assistanttext" : "assistantstep", "utf8text", i.ToString()));
        var result = await resultTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.InRange(peak, 1, 4);
        Assert.Equal(8, calls);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i.ToString()), result.Select(item => item.Content));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? "assistanttext" : "assistantstep"), result.Select(item => item.Kind));
    }

    [Fact]
    public async Task A_failed_read_cancels_and_drains_other_reads_without_returning_partial_text()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cancelled = 0;
        var result = ChatAssistantPayloadReader.ReadAsync(["bad", "one", "two", "three", "never"], async (id, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 4) started.TrySetResult();
            if (id == "bad")
            {
                await fail.Task;
                throw new AgentApiException("payload_run_mismatch");
            }
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
            return new("assistanttext", "utf8text", id);
        }, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fail.SetResult();
        var error = await Assert.ThrowsAsync<AgentApiException>(() => result.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal("payload_run_mismatch", error.ErrorCode);
        Assert.Equal(4, calls);
        Assert.Equal(3, cancelled);
    }

    [Fact]
    public async Task Cancellation_stops_all_in_flight_reads_before_returning()
    {
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var task = ChatAssistantPayloadReader.ReadAsync(["a", "b", "c", "d", "e"], async (id, ct) =>
        {
            if (Interlocked.Increment(ref active) == 4) started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); return new("assistanttext", "utf8text", id); }
            finally { Interlocked.Decrement(ref active); }
        }, stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task No_payloads_need_no_reads()
    {
        var result = await ChatAssistantPayloadReader.ReadAsync([], (_, _) => throw new InvalidOperationException("No request expected"), default);
        Assert.Empty(result);
    }
}
