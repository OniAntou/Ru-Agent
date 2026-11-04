using System.Linq;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class MessageRailViewportTests
{
    [Fact]
    public void A_chat_that_fits_the_screen_marks_all_visible_messages_and_emphasizes_the_tail()
    {
        var selection = MessageRailPolicy.SelectViewport(7, 0, 6, 0, 0);
        Assert.Equal(new MessageRailSelection(0, 6), selection);
    }

    [Fact]
    public void Reading_the_middle_tracks_the_visible_range_instead_of_the_first_row_only()
    {
        var selection = MessageRailPolicy.SelectViewport(20, 8, 11, 400, 1000);
        Assert.Equal(new MessageRailSelection(8, 11), selection);
    }

    [Fact]
    public void Being_near_the_bottom_does_not_light_an_offscreen_tail()
    {
        var selection = MessageRailPolicy.SelectViewport(20, 14, 17, 950, 1000);
        Assert.Equal(new MessageRailSelection(14, 17), selection);
    }

    [Fact]
    public void Settling_at_the_bottom_prioritizes_the_tail_when_panel_indices_still_belong_to_old_layout()
    {
        var selection = MessageRailPolicy.SelectViewport(20, 0, 3, 1000, 1000);
        Assert.Equal(new MessageRailSelection(19, 19), selection);
    }

    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(20, -1, -1, 0, 1000)]
    [InlineData(20, 25, 28, 0, 1000)]
    [InlineData(20, 5, 3, 0, 1000)]
    [InlineData(20, 0, 3, double.NaN, 1000)]
    public void Missing_or_stale_viewport_data_clears_the_highlight(int count, int first, int last, double offset, double maximum)
        => Assert.Equal(MessageRailSelection.Empty, MessageRailPolicy.SelectViewport(count, first, last, offset, maximum));

    [Fact]
    public void Grouped_ticks_cover_the_visible_messages_without_lighting_unrelated_history()
    {
        var selection = MessageRailPolicy.SelectViewport(1000, 505, 523, 5000, 10000);
        var lit = Enumerable.Range(0, 100).Where(tick => MessageRailPolicy.IsVisibleTick(tick, 100, 1000, selection));
        Assert.Equal(new[] { 50, 51, 52 }, lit);
        Assert.False(MessageRailPolicy.IsVisibleTick(0, 100, 1000, MessageRailSelection.Empty));
    }

    [Fact]
    public void Resize_and_history_replacement_recompute_the_visible_marks_from_the_latest_snapshot()
    {
        var original = MessageRailPolicy.SelectViewport(20, 8, 11, 400, 1000);
        var resized = MessageRailPolicy.SelectViewport(20, 8, 14, 400, 1000);
        var newChat = MessageRailPolicy.SelectViewport(2, 0, 1, 0, 0);
        Assert.Equal(11, original.LastMessage);
        Assert.Equal(14, resized.LastMessage);
        Assert.Equal(new MessageRailSelection(0, 1), newChat);
    }
}
