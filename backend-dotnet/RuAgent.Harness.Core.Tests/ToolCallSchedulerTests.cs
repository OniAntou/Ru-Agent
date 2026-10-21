using System.Text.Json;
using Xunit;

namespace RuAgent.Harness.Core.Tests;

public sealed class ToolCallSchedulerTests
{
    [Fact]
    public void Assistant_step_accepts_and_defensively_copies_multiple_calls()
    {
        var calls = new List<ModelToolCall>
        {
            ModelCall("files.read", "README.md"),
            ModelCall("files.list", "docs"),
        };

        var step = new AssistantStep(
            "inspect",
            calls,
            AssistantFinishReason.ToolCalls,
            32);
        calls.Clear();

        Assert.Equal(2, step.ToolCalls.Count);
        Assert.False(step.ToolCalls is ModelToolCall[]);
    }

    [Fact]
    public void Model_tool_call_requires_bounded_object_arguments_and_nonempty_provider_id()
    {
        Assert.Throws<ArgumentException>(() => new ModelToolCall(
            string.Empty,
            "files.read",
            "1",
            JsonDocument.Parse("{}").RootElement.Clone()));
        var oversized = JsonDocument.Parse(
            $"{{\"value\":\"{new string('a', ModelToolCall.MaximumArgumentBytes)}\"}}")
            .RootElement
            .Clone();

        Assert.Throws<ArgumentException>(() => new ModelToolCall(
            "provider-call",
            "files.read",
            "1",
            oversized));
    }

    [Fact]
    public void Tool_decision_factories_reject_unknown_access_and_unsafe_codes()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolProposalDecision.Ask(
            (ToolAccessKind)int.MaxValue,
            new ApprovalId("apr_11111111111111111111111111111111")));
        Assert.Throws<ArgumentOutOfRangeException>(() => ToolProposalDecision.Deny(
            (ToolAccessKind)int.MaxValue,
            "denied"));
        Assert.Throws<ArgumentException>(() => ToolProposalDecision.Deny(
            ToolAccessKind.ReadOnly,
            "raw private text"));
    }

    [Fact]
    public void Deny_hint_is_bounded_and_sanitized()
    {
        var decision = ToolProposalDecision.Deny(
            ToolAccessKind.ReadOnly,
            "malformed_call",
            "read requires op.\r\n" + new string('x', 400));

        Assert.NotNull(decision.DenyHint);
        Assert.DoesNotContain('\n', decision.DenyHint!);
        Assert.True(decision.DenyHint!.Length <= ToolProposalDecision.MaximumDenyHintLength);
        Assert.Null(ToolProposalDecision.Deny(ToolAccessKind.ReadOnly, "policy_denied").DenyHint);
    }

    [Fact]
    public async Task Consecutive_read_only_calls_overlap_and_receipts_return_in_model_order()
    {
        var scheduler = new ToolCallScheduler();
        var firstStarted = NewSignal();
        var secondStarted = NewSignal();
        var release = NewSignal();
        var calls = new[]
        {
            Prepared(1, ToolAccessKind.ReadOnly, '1'),
            Prepared(2, ToolAccessKind.ReadOnly, '2'),
        };

        var scheduling = scheduler.ExecuteAsync(
            calls,
            async (call, cancellationToken) =>
            {
                (call.ModelOrder == 1 ? firstStarted : secondStarted).TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return Receipt(call);
            },
            CancellationToken.None);

        await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(scheduling.IsCompleted);
        release.TrySetResult();
        var receipts = await scheduling;

        Assert.Equal(calls.Select(call => call.ExecutionId), receipts.Select(receipt => receipt.ExecutionId));
    }

    [Fact]
    public async Task Mutation_and_terminal_calls_are_serialized_in_model_order()
    {
        var scheduler = new ToolCallScheduler();
        var concurrent = 0;
        var maximumConcurrent = 0;
        var starts = new List<int>();
        var calls = new[]
        {
            Prepared(3, ToolAccessKind.Terminal, '3'),
            Prepared(1, ToolAccessKind.Mutation, '1'),
            Prepared(2, ToolAccessKind.Mutation, '2'),
        };

        var receipts = await scheduler.ExecuteAsync(
            calls,
            async (call, cancellationToken) =>
            {
                starts.Add(call.ModelOrder);
                var active = Interlocked.Increment(ref concurrent);
                maximumConcurrent = Math.Max(maximumConcurrent, active);
                await Task.Delay(10, cancellationToken);
                Interlocked.Decrement(ref concurrent);
                return Receipt(call);
            },
            CancellationToken.None);

        Assert.Equal(new[] { 1, 2, 3 }, starts);
        Assert.Equal(1, maximumConcurrent);
        Assert.Equal(new[] { 1, 2, 3 }, receipts.Select(ReceiptOrder));
    }

    [Fact]
    public async Task Serial_call_is_a_barrier_between_read_batches()
    {
        var scheduler = new ToolCallScheduler();
        var firstReadStarted = NewSignal();
        var secondReadStarted = NewSignal();
        var releaseFirstReads = NewSignal();
        var mutationStarted = NewSignal();
        var releaseMutation = NewSignal();
        var finalReadStarted = NewSignal();
        var calls = new[]
        {
            Prepared(1, ToolAccessKind.ReadOnly, '1'),
            Prepared(2, ToolAccessKind.ReadOnly, '2'),
            Prepared(3, ToolAccessKind.Mutation, '3'),
            Prepared(4, ToolAccessKind.ReadOnly, '4'),
        };

        var scheduling = scheduler.ExecuteAsync(
            calls,
            async (call, cancellationToken) =>
            {
                if (call.ModelOrder <= 2)
                {
                    (call.ModelOrder == 1 ? firstReadStarted : secondReadStarted).TrySetResult();
                    await releaseFirstReads.Task.WaitAsync(cancellationToken);
                }
                else if (call.ModelOrder == 3)
                {
                    mutationStarted.TrySetResult();
                    await releaseMutation.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    finalReadStarted.TrySetResult();
                }

                return Receipt(call);
            },
            CancellationToken.None);

        await Task.WhenAll(firstReadStarted.Task, secondReadStarted.Task)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(mutationStarted.Task.IsCompleted);
        Assert.False(finalReadStarted.Task.IsCompleted);
        releaseFirstReads.TrySetResult();
        await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(finalReadStarted.Task.IsCompleted);
        releaseMutation.TrySetResult();
        await finalReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await scheduling;
    }

    [Fact]
    public async Task Read_only_chunks_never_exceed_eight_and_keep_model_order()
    {
        var scheduler = new ToolCallScheduler();
        var calls = Enumerable.Range(1, 12)
            .Select(index => PreparedForLargeBatch(index))
            .ToArray();
        var active = 0;
        var maximumActive = 0;

        var receipts = await scheduler.ExecuteAsync(
            calls,
            async (call, cancellationToken) =>
            {
                var current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                await Task.Delay(20, cancellationToken);
                Interlocked.Decrement(ref active);
                return Receipt(call);
            },
            CancellationToken.None);

        Assert.Equal(ToolCallScheduler.MaxReadConcurrency, maximumActive);
        Assert.True(maximumActive > 1);
        Assert.Equal(calls.Select(call => call.ExecutionId), receipts.Select(receipt => receipt.ExecutionId));
    }

    [Fact]
    public async Task OutcomeUnknown_drains_started_reads_and_stops_the_mutation_barrier()
    {
        var scheduler = new ToolCallScheduler();
        var firstChunkStarted = NewSignal();
        var releaseFirstChunk = NewSignal();
        var calls = Enumerable.Range(1, 10)
            .Select(index => PreparedForLargeBatch(
                index,
                index == 10 ? ToolAccessKind.Mutation : ToolAccessKind.ReadOnly))
            .ToArray();
        var startedInFirstChunk = 0;
        var startedAfterChunk = 0;

        var scheduling = scheduler.ExecuteAsync(
            calls,
            async (call, cancellationToken) =>
            {
                if (call.ModelOrder <= ToolCallScheduler.MaxReadConcurrency)
                {
                    if (Interlocked.Increment(ref startedInFirstChunk)
                        == ToolCallScheduler.MaxReadConcurrency)
                    {
                        firstChunkStarted.TrySetResult();
                    }

                    await releaseFirstChunk.Task.WaitAsync(cancellationToken);
                    return call.ModelOrder == 1
                        ? new ToolExecutionReceipt(
                            call.ExecutionId,
                            call.ToolCallId,
                            ToolExecutionOutcome.OutcomeUnknown,
                            "transport_failure",
                            null,
                            sideEffectsPossible: false)
                        : Receipt(call);
                }

                // A rolling pool may admit the ninth read before the first read
                // reports its unknown outcome. It must still drain that receipt.
                Assert.Equal(ToolAccessKind.ReadOnly, call.AccessKind);
                Interlocked.Increment(ref startedAfterChunk);
                return Receipt(call);
            },
            CancellationToken.None);

        await firstChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        releaseFirstChunk.TrySetResult();
        var receipts = await scheduling.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.InRange(startedAfterChunk, 0, 1);
        Assert.Equal(8 + startedAfterChunk, receipts.Count);
        Assert.Equal(calls.Take(receipts.Count).Select(call => call.ToolCallId), receipts.Select(receipt => receipt.ToolCallId));
        Assert.Equal(ToolExecutionOutcome.OutcomeUnknown, receipts[0].Outcome);
    }

    [Fact]
    public async Task Read_chunks_release_a_mutation_barrier_before_following_reads()
    {
        var scheduler = new ToolCallScheduler();
        var firstChunkStarted = NewSignal();
        var readsStarted = NewSignal();
        var releaseReads = NewSignal();
        var mutationStarted = NewSignal();
        var releaseMutation = NewSignal();
        var finalReadStarted = NewSignal();
        var calls = Enumerable.Range(1, 13)
            .Select(index => PreparedForLargeBatch(index, index == 11
                ? ToolAccessKind.Mutation
                : ToolAccessKind.ReadOnly))
            .ToArray();
        var activeReads = 0;
        var maximumReads = 0;
        var readCount = 0;

        var scheduling = scheduler.ExecuteAsync(
            calls,
            async (call, cancellationToken) =>
            {
                if (call.AccessKind == ToolAccessKind.ReadOnly && call.ModelOrder <= 10)
                {
                    var current = Interlocked.Increment(ref activeReads);
                    maximumReads = Math.Max(maximumReads, current);
                    var currentReadCount = Interlocked.Increment(ref readCount);
                    if (currentReadCount == ToolCallScheduler.MaxReadConcurrency)
                    {
                        firstChunkStarted.TrySetResult();
                    }

                    if (currentReadCount == 10)
                    {
                        readsStarted.TrySetResult();
                    }

                    await releaseReads.Task.WaitAsync(cancellationToken);
                    Interlocked.Decrement(ref activeReads);
                }
                else if (call.AccessKind == ToolAccessKind.Mutation)
                {
                    mutationStarted.TrySetResult();
                    await releaseMutation.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    finalReadStarted.TrySetResult();
                }

                return Receipt(call);
            },
            CancellationToken.None);

        await firstChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(mutationStarted.Task.IsCompleted);
        releaseReads.TrySetResult();
        await readsStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await mutationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(finalReadStarted.Task.IsCompleted);
        releaseMutation.TrySetResult();
        await finalReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var receipts = await scheduling;

        Assert.Equal(ToolCallScheduler.MaxReadConcurrency, maximumReads);
        Assert.Equal(calls.Select(call => call.ExecutionId), receipts.Select(receipt => receipt.ExecutionId));
    }

    [Fact]
    public async Task Duplicate_model_order_or_tool_identity_fails_closed()
    {
        var scheduler = new ToolCallScheduler();
        var first = Prepared(1, ToolAccessKind.ReadOnly, '1');
        var duplicateOrder = Prepared(1, ToolAccessKind.ReadOnly, '2');
        var duplicateIdentity = new PreparedToolExecution(
            new ExecutionId("exec_33333333333333333333333333333333"),
            first.ToolCallId,
            2,
            ToolAccessKind.ReadOnly,
            "prepared-3",
            Array.Empty<PreparedPayload>());

        var orderError = await Assert.ThrowsAsync<ArgumentException>(() =>
            scheduler.ExecuteAsync(
                new[] { first, duplicateOrder },
                (call, cancellationToken) => Task.FromResult(Receipt(call)),
                CancellationToken.None));
        var identityError = await Assert.ThrowsAsync<ArgumentException>(() =>
            scheduler.ExecuteAsync(
                new[] { first, duplicateIdentity },
                (call, cancellationToken) => Task.FromResult(Receipt(call)),
                CancellationToken.None));

        Assert.Equal("duplicate_tool_model_order", orderError.Message);
        Assert.Equal("duplicate_tool_identity", identityError.Message);
    }

    [Fact]
    public async Task Unknown_mutation_outcome_stops_later_serial_calls()
    {
        var scheduler = new ToolCallScheduler();
        var calls = new[]
        {
            Prepared(1, ToolAccessKind.Mutation, '1'),
            Prepared(2, ToolAccessKind.Mutation, '2'),
        };
        var executed = new List<int>();

        var receipts = await scheduler.ExecuteAsync(
            calls,
            (call, cancellationToken) =>
            {
                executed.Add(call.ModelOrder);
                return Task.FromResult(new ToolExecutionReceipt(
                    call.ExecutionId,
                    call.ToolCallId,
                    ToolExecutionOutcome.OutcomeUnknown,
                    "transport_failure",
                    null,
                    sideEffectsPossible: true));
            },
            CancellationToken.None);

        Assert.Equal(new[] { 1 }, executed);
        Assert.Single(receipts);
    }

    [Fact]
    public async Task Null_scheduled_call_fails_with_a_bounded_contract_error()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            new ToolCallScheduler().ExecuteAsync(
                new PreparedToolExecution[] { null! },
                (call, cancellationToken) => Task.FromResult(Receipt(call)),
                CancellationToken.None));

        Assert.Equal("scheduled_tool_call_null", error.Message);
    }

    private static ModelToolCall ModelCall(string toolId, string path) =>
        new(
            $"provider-{toolId}",
            toolId,
            "1",
            JsonDocument.Parse($"{{\"path\":\"{path}\"}}").RootElement.Clone());

    private static PreparedToolExecution Prepared(
        int modelOrder,
        ToolAccessKind accessKind,
        char id) =>
        new(
            new ExecutionId($"exec_{new string(id, 32)}"),
            new ToolCallId($"call_{new string(id, 32)}"),
            modelOrder,
            accessKind,
            $"prepared-{id}",
            Array.Empty<PreparedPayload>());

    private static PreparedToolExecution PreparedForLargeBatch(
        int modelOrder,
        ToolAccessKind accessKind = ToolAccessKind.ReadOnly)
    {
        var suffix = modelOrder.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
        return new PreparedToolExecution(
            new ExecutionId($"exec_{new string('a', 31)}{suffix}"),
            new ToolCallId($"call_{new string('b', 31)}{suffix}"),
            modelOrder,
            accessKind,
            $"prepared-{modelOrder}",
            Array.Empty<PreparedPayload>());
    }

    private static ToolExecutionReceipt Receipt(PreparedToolExecution call) =>
        new(
            call.ExecutionId,
            call.ToolCallId,
            ToolExecutionOutcome.Succeeded,
            $"order_{call.ModelOrder}",
            null,
            sideEffectsPossible: false);

    private static int ReceiptOrder(ToolExecutionReceipt receipt) =>
        int.Parse(receipt.Code.AsSpan("order_".Length));

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
