using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatScrollPolicyTests
{
    [Theory]
    [InlineData(900, 600)]
    [InlineData(-1, 600)]
    [InlineData(double.NaN, 600)]
    [InlineData(900, double.PositiveInfinity)]
    public void Following_never_targets_an_inconsistent_extent(double offset, double maximum)
    {
        Assert.False(ChatScrollPolicy.CanFollow(offset, maximum, following: true));
    }

    [Theory]
    [InlineData(true, 900, 600)]
    [InlineData(false, 900, 600)]
    [InlineData(false, 0, 0)]
    public void Transient_layout_does_not_change_the_readers_follow_intent(bool following, double offset, double maximum)
    {
        Assert.Equal(following, ChatScrollPolicy.FollowAfterViewChange(offset, maximum, following));
    }

    [Fact]
    public void Reading_history_stays_parked_until_the_reader_reaches_the_bottom()
    {
        Assert.False(ChatScrollPolicy.CanFollow(300, 1000, following: false));
        Assert.False(ChatScrollPolicy.FollowAfterViewChange(400, 1000, following: false));
        Assert.True(ChatScrollPolicy.FollowAfterViewChange(1000, 1000, following: false));
        Assert.True(ChatScrollPolicy.CanFollow(1000, 1100, following: true));
    }

    [Theory]
    [InlineData(940)]
    [InlineData(980)]
    [InlineData(998)]
    public void Manual_reading_near_the_bottom_does_not_rearm_layout_follow(double offset)
    {
        Assert.True(ChatScrollPolicy.IsNearBottom(offset, 1000));
        Assert.False(ChatScrollPolicy.FollowAfterViewChange(offset, 1000, following: false));
        Assert.True(ChatScrollPolicy.FollowAfterViewChange(1000, 1000, following: false));
    }

    [Fact]
    public void Layout_follow_tracks_real_tail_geometry_and_unsubscribes_on_disposal()
    {
        var code = GuiSource.ViewText("ChatView");
        Assert.Contains("LayoutUpdated += OnTranscriptLayoutUpdated", GuiSource.MethodBody(code, "InitializeChatScrolling"));
        Assert.Contains("LayoutUpdated -= OnTranscriptLayoutUpdated", GuiSource.MethodBody(code, "DisposeChatScrolling"));
        var layout = GuiSource.MethodBody(code, "OnTranscriptLayoutUpdated");
        Assert.Contains("!_followOutput", layout);
        Assert.Contains("_handlingTranscriptLayout", layout);
        Assert.Contains("TransformToVisual", layout);
        Assert.Contains("RestoreAnchorOffset", layout);
        Assert.Contains("disableAnimation: true", layout);
        Assert.DoesNotContain("UpdateLayout()", layout);
        Assert.Contains("if (!_followOutput && e.IsIntermediate) return", GuiSource.MethodBody(code, "OnChatScrollViewChanged"));
    }

    [Fact]
    public void New_layout_extent_does_not_stop_following_without_reader_input()
    {
        Assert.True(ChatScrollPolicy.FollowAfterViewChange(1000, 1400, following: true));
        // Wheel/pointer/navigation input clears the latch in the view first.
        Assert.False(ChatScrollPolicy.FollowAfterViewChange(1000, 1400, following: false));
    }

    [Fact]
    public void Virtualized_extent_changes_keep_offsets_and_settle_active_follow()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var panel = GuiSource.NamedElements(document)["ChatListView"]
            .Descendants().Single(element => element.Name.LocalName == "ItemsStackPanel");
        Assert.Equal("KeepScrollOffset", (string?)panel.Attribute("ItemsUpdatingScrollMode"));
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OnChatScrollViewChanged");
        Assert.Contains("!e.IsIntermediate", body, StringComparison.Ordinal);
        Assert.Contains("QueueScrollRetry()", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Reader_input_is_wired_before_the_view_loads_its_scroll_host()
    {
        var text = GuiSource.ViewText("ChatView");
        Assert.True(text.IndexOf("InitializeChatScrolling();", StringComparison.Ordinal)
            < text.IndexOf("this.Loaded +=", StringComparison.Ordinal));
        var initialize = GuiSource.MethodBody(text, "InitializeChatScrolling");
        Assert.Contains("PointerWheelChangedEvent", initialize, StringComparison.Ordinal);
        Assert.Contains("PointerPressedEvent", initialize, StringComparison.Ordinal);
        Assert.Contains("KeyDownEvent", initialize, StringComparison.Ordinal);
        var dispose = GuiSource.MethodBody(text, "DisposeChatScrolling");
        Assert.DoesNotContain("if (!_newMessagesPillWired) return", dispose, StringComparison.Ordinal);
    }

    [Fact]
    public void Bulk_layout_callback_flushes_layout_and_checks_its_session_before_scrolling()
    {
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "RunAfterLayout");
        Assert.Contains("ChatListView.UpdateLayout()", body, StringComparison.Ordinal);
        Assert.Contains("_sessionOpenGeneration", body, StringComparison.Ordinal);
        Assert.Contains("_scrollInteractionGeneration", body, StringComparison.Ordinal);
        Assert.True(body.IndexOf("UpdateLayout()", StringComparison.Ordinal) < body.IndexOf("action()", StringComparison.Ordinal));
    }

    [Fact]
    public void Session_open_targets_the_latest_item_instead_of_an_estimated_pixel_extent()
    {
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "ScrollToLatestAfterLayout");
        Assert.Contains("ScrollIntoView", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Session_open_positions_after_layout_in_the_replacement_dispatcher_turn()
    {
        var code = GuiSource.ViewText("ChatView");
        var open = GuiSource.MethodBody(code, "OpenSessionAsync");
        Assert.True(open.IndexOf("Messages.ReplaceAll(opened)", StringComparison.Ordinal)
            < open.IndexOf("PositionOpenedTranscript()", StringComparison.Ordinal));
        var position = GuiSource.MethodBody(code, "PositionOpenedTranscript");
        Assert.True(position.IndexOf("UpdateLayout()", StringComparison.Ordinal)
            < position.IndexOf("ScrollToLatestAfterLayout()", StringComparison.Ordinal));
        Assert.DoesNotContain("TryEnqueue", position);
        Assert.DoesNotContain("await ", position);
    }

    [Fact]
    public void Latest_row_stays_anchored_until_reader_input_releases_it()
    {
        var code = GuiSource.ViewText("ChatView");
        var latest = GuiSource.MethodBody(code, "ScrollToLatestAfterLayout");
        Assert.Contains("ItemsUpdatingScrollMode.KeepLastItemInView", latest);
        Assert.Contains("disableAnimation: true", latest);
        var stop = GuiSource.MethodBody(code, "StopFollowingTranscript");
        Assert.Contains("ItemsUpdatingScrollMode.KeepScrollOffset", stop);
        Assert.Contains("_followOutput = false", stop);
        Assert.Contains("_scrollInteractionGeneration++", stop);
        Assert.Contains("StopFollowingTranscript()", GuiSource.MethodBody(code, "OnTranscriptScrollKey"));
        Assert.Contains("=> StopFollowingTranscript()", code);
    }

    [Theory]
    [InlineData(700, 0, -30, 2000, 730)]
    [InlineData(700, 0, 20, 2000, 680)]
    [InlineData(0, 0, 20, 2000, 0)]
    [InlineData(700, 0, -30, 710, 710)]
    public void Restore_keeps_the_visible_item_at_its_previous_screen_position(double offset, double y, double savedY, double maximum, double expected)
    {
        Assert.Equal(expected, ChatScrollPolicy.RestoreAnchorOffset(offset, y, savedY, maximum));
    }
}
