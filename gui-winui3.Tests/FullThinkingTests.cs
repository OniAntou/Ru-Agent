using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// An opened thinking block shows the whole reasoning, read on demand when the row's
/// excerpt was cut, and the live line changes smoothly instead of jumping.
/// </summary>
public sealed class FullThinkingTests
{
    private const string Payload = "payload_0123456789abcdef0123456789abcdef";

    [Fact]
    public void A_cut_excerpt_is_replaced_by_the_whole_reasoning_a_slice_at_a_time()
    {
        var item = Thinking(detail: "The start…", payload: Payload);
        Assert.Equal("The start…", item.ThinkingText);
        Assert.True(item.TryBeginFullThinkingLoad());
        Assert.False(item.TryBeginFullThinkingLoad());
        Assert.Equal("Loading the full thinking…", item.ThinkingLoadStatus);

        var full = "The start. " + new string('x', AgentActivityItem.ThinkingSliceCharacters * 2);
        item.CompleteFullThinkingLoad(full);

        Assert.False(item.HasThinkingLoadStatus);
        Assert.Equal(AgentActivityItem.ThinkingSliceCharacters, item.ThinkingText.Length);
        Assert.True(item.HasMoreThinking);
        item.ShowMoreThinking();
        item.ShowMoreThinking();
        Assert.Equal(full, item.ThinkingText);
        Assert.False(item.HasMoreThinking);
        Assert.False(item.NeedsFullThinking);
    }

    [Fact]
    public void A_failed_read_keeps_the_excerpt_and_says_so()
    {
        var item = Thinking(detail: "The start…", payload: Payload);
        Assert.True(item.TryBeginFullThinkingLoad());

        item.CompleteFullThinkingLoad(null);

        Assert.Equal("The start…", item.ThinkingText);
        Assert.Equal("Couldn't load the rest of this thinking.", item.ThinkingLoadStatus);
        // Opening the block again tries again.
        Assert.True(item.NeedsFullThinking);
    }

    [Fact]
    public void A_whole_excerpt_needs_no_read_and_a_later_frame_keeps_the_named_payload()
    {
        Assert.False(Thinking(detail: "Short.", payload: null).NeedsFullThinking);

        var item = Thinking(detail: "The start…", payload: Payload);
        item.UpdateFrom(Model(AgentActivityState.Completed, "The start…", payload: null));
        Assert.Equal(Payload, item.ReasoningPayloadId);
    }

    [Fact]
    public void The_opened_block_binds_the_whole_reasoning_and_offers_more()
    {
        var chat = GuiSource.Text("Views", "ChatView.xaml");
        Assert.Contains("Text=\"{x:Bind ThinkingText, Mode=OneWay}\"", chat, StringComparison.Ordinal);
        Assert.Contains("Click=\"OnShowMoreThinkingClick\"", chat, StringComparison.Ordinal);
        Assert.Contains("{x:Bind ThinkingLoadStatus, Mode=OneWay}", chat, StringComparison.Ordinal);
    }

    [Fact]
    public void Live_lines_are_coalesced_and_fade_in_on_a_fixed_scale_shimmer()
    {
        var feed = GuiSource.Text("Views", "ChatView.AgentFeed.cs");
        // One dispatcher turn per burst, and a flush only for a row the feed has not seen.
        var receive = GuiSource.MethodBody(feed, "OnAgentReasoningPreviewReceived");
        Assert.DoesNotContain("FlushAgentActivities", receive, StringComparison.Ordinal);
        Assert.Contains("_thinkingPreviewDrainQueued", receive, StringComparison.Ordinal);
        Assert.Contains("FlushAgentActivities", GuiSource.MethodBody(feed, "DrainThinkingPreviews"), StringComparison.Ordinal);
        Assert.Contains("BrushMappingMode.Absolute", GuiSource.MethodBody(feed, "UseFixedScaleSweep"), StringComparison.Ordinal);
        Assert.Contains("UiTiming.AnimationsEnabled", GuiSource.MethodBody(feed, "FadeInThinkingLine"), StringComparison.Ordinal);
    }

    private static AgentActivityItem Thinking(string detail, string? payload) =>
        new(Model(AgentActivityState.Completed, detail, payload));

    private static AgentActivityModel Model(AgentActivityState state, string detail, string? payload) =>
        new("act_think", AgentActivityKind.Thinking, state, null, null, null, null, null, null, null, 1200, "succeeded",
            null, null, null, detail, ReasoningPayloadId: payload);
}
