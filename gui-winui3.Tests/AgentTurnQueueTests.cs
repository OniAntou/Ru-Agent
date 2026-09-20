using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentTurnQueueTests
{
    [Fact]
    public void Queued_turns_keep_the_order_they_were_typed()
    {
        var queue = new AgentTurnQueue();

        Assert.True(queue.TryEnqueue("first", "first", null));
        Assert.True(queue.TryEnqueue("second", "second", null));

        Assert.Equal(2, queue.Items.Count);
        Assert.Equal("first", queue.TakeFirst()!.Text);
        Assert.Equal("second", queue.TakeFirst()!.Text);
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void Enqueue_refuses_a_turn_past_the_limit()
    {
        var queue = new AgentTurnQueue();
        for (int index = 0; index < AgentTurnQueuePolicy.MaxTurns; index++)
        {
            Assert.True(queue.TryEnqueue($"turn {index}", $"turn {index}", null));
        }

        Assert.False(queue.TryEnqueue("one too many", "one too many", null));
        Assert.Equal(AgentTurnQueuePolicy.MaxTurns, queue.Items.Count);
    }

    [Fact]
    public void Enqueue_refuses_a_turn_with_neither_text_nor_attachment()
    {
        var queue = new AgentTurnQueue();

        Assert.False(queue.TryEnqueue(string.Empty, string.Empty, null));
        Assert.False(queue.TryEnqueue("   ", string.Empty, null));
        Assert.True(queue.IsEmpty);
    }

    [Fact]
    public void The_stop_hold_suppresses_exactly_one_drain()
    {
        var queue = new AgentTurnQueue();
        queue.TryEnqueue("next", "next", null);
        queue.HoldAfterStop();

        // The completion the user caused: the queued turn must not start itself.
        Assert.False(queue.TryBeginAutoDrain());
        Assert.False(queue.IsHeldAfterStop);
        Assert.Single(queue.Items);

        // A later completion drains normally.
        Assert.True(queue.TryBeginAutoDrain());
        Assert.Single(queue.Items);
    }

    [Fact]
    public void An_empty_queue_never_drains()
    {
        var queue = new AgentTurnQueue();

        Assert.False(queue.TryBeginAutoDrain());
        Assert.Empty(queue.Items);
    }

    [Fact]
    public void Remove_drops_the_exact_turn_even_when_two_are_identical()
    {
        var queue = new AgentTurnQueue();
        queue.TryEnqueue("same", "same", null);
        queue.TryEnqueue("same", "same", null);
        var first = queue.Items[0];
        var second = queue.Items[1];

        Assert.True(queue.Remove(second));
        Assert.Single(queue.Items);
        Assert.Same(first, queue.Items[0]);
    }

    [Fact]
    public void Clear_drops_turns_and_the_stop_hold()
    {
        var queue = new AgentTurnQueue();
        queue.TryEnqueue("next", "next", null);
        queue.HoldAfterStop();

        queue.Clear();

        Assert.True(queue.IsEmpty);
        Assert.False(queue.IsHeldAfterStop);
        Assert.False(queue.TryBeginAutoDrain());
    }

    [Fact]
    public void A_turn_carries_its_attachments_and_says_so()
    {
        var queue = new AgentTurnQueue();
        var attachment = new ImageAttachmentItem { FileName = "shot.png" };

        Assert.True(queue.TryEnqueue("look", "look", new[] { attachment }));

        var queued = queue.Items[0];
        Assert.True(queued.HasAttachments);
        Assert.Same(attachment, queued.Attachments[0]);
    }

    [Fact]
    public void Preview_flattens_a_multiline_turn_to_one_line()
    {
        Assert.Equal(
            "fix the parser then run tests",
            AgentTurnQueuePolicy.PreviewLabel("fix the parser\nthen run tests"));
    }

    [Fact]
    public void Preview_trims_a_long_turn_with_an_ellipsis()
    {
        string long_ = new string('a', AgentTurnQueuePolicy.MaxLabelLength + 20);

        string label = AgentTurnQueuePolicy.PreviewLabel(long_);

        Assert.Equal(AgentTurnQueuePolicy.MaxLabelLength, label.Length);
        Assert.EndsWith("…", label, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_falls_back_to_the_prompt_and_then_to_the_attachment_wording()
    {
        Assert.Equal("the prompt", AgentTurnQueuePolicy.PreviewLabel(null, "the prompt"));
        Assert.Equal("Attachment", AgentTurnQueuePolicy.PreviewLabel("   ", "   "));
    }
}
