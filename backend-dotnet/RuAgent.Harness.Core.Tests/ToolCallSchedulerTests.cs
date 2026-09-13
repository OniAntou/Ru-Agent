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
            null,
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
            null,
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
