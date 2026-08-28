using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatTurnQueueTests
{
    [Fact]
    public void Queue_activates_turns_in_fifo_order()
    {
        var queue = new ChatTurnQueue(capacity: 2);
        Assert.True(queue.TryEnqueue("session-1", "c_first123", out var first));
        Assert.True(queue.TryEnqueue("session-1", "c_second12", out var second));

        Assert.Equal(first, queue.ActiveTurn);
        queue.Complete(first.CorrelationId);
        Assert.Equal(second, queue.ActiveTurn);
    }

    [Fact]
    public void Queue_rejects_turns_after_capacity_without_mutating_order()
    {
        var queue = new ChatTurnQueue(capacity: 2);
        Assert.True(queue.TryEnqueue("session-1", "c_first123", out _));
        Assert.True(queue.TryEnqueue("session-1", "c_second12", out _));

        Assert.False(queue.TryEnqueue("session-1", "c_third123", out _));
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void Invalidate_drops_old_generation_and_stale_correlation()
    {
        var queue = new ChatTurnQueue();
        Assert.True(queue.TryEnqueue("session-1", "c_first123", out var first));

        queue.Invalidate();

        Assert.Null(queue.ActiveTurn);
        Assert.False(queue.TryGetActive("c_first123", out _));
        Assert.True(queue.TryEnqueue("session-1", "c_second12", out var second));
        Assert.True(second.Generation > first.Generation);
    }

    [Fact]
    public void Remove_can_cancel_a_pending_turn_without_promoting_it()
    {
        var queue = new ChatTurnQueue();
        Assert.True(queue.TryEnqueue("session-1", "c_first123", out var first));
        Assert.True(queue.TryEnqueue("session-1", "c_second12", out var second));

        Assert.True(queue.Remove(second.CorrelationId));
        Assert.Equal(1, queue.Count);
        Assert.Equal(first, queue.ActiveTurn);
        Assert.False(queue.Remove(second.CorrelationId));
    }
}
