using System.Linq;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>The transcript's message rail: tick geometry, grouping and hover previews.</summary>
public sealed class MessageRailPolicyTests
{
    [Fact]
    public void A_short_transcript_gets_one_tick_per_message_at_full_pitch()
    {
        var ticks = MessageRailPolicy.TickCount(12, 600);
        Assert.Equal(12, ticks);
        Assert.Equal(MessageRailPolicy.MaxPitch, MessageRailPolicy.Pitch(ticks, 600));
    }

    [Fact]
    public void A_long_transcript_groups_messages_so_the_rail_fits_its_host()
    {
        var ticks = MessageRailPolicy.TickCount(1000, 400);
        Assert.Equal(100, ticks);
        Assert.True(ticks * MessageRailPolicy.Pitch(ticks, 400) <= 400);
    }

    [Theory]
    [InlineData(0, 600)]
    [InlineData(5, 0)]
    [InlineData(5, double.NaN)]
    public void Nothing_to_draw_yields_no_ticks(int messages, double height)
    {
        Assert.Equal(0, MessageRailPolicy.TickCount(messages, height));
    }

    [Theory]
    [InlineData(10, 4)]
    [InlineData(10, 10)]
    [InlineData(1000, 37)]
    [InlineData(7, 3)]
    public void Every_message_maps_to_the_tick_whose_range_holds_it(int messages, int ticks)
    {
        for (var message = 0; message < messages; message++)
        {
            var tick = MessageRailPolicy.TickOfMessage(message, ticks, messages);
            Assert.InRange(tick, 0, ticks - 1);
            Assert.True(MessageRailPolicy.FirstMessageOfTick(tick, ticks, messages) <= message);
            if (tick + 1 < ticks)
                Assert.True(message < MessageRailPolicy.FirstMessageOfTick(tick + 1, ticks, messages));
        }

        Assert.All(Enumerable.Range(0, ticks), tick =>
            Assert.Equal(tick, MessageRailPolicy.TickOfMessage(MessageRailPolicy.FirstMessageOfTick(tick, ticks, messages), ticks, messages)));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(0, 0)]
    [InlineData(9.9, 0)]
    [InlineData(10, 1)]
    [InlineData(55, 5)]
    [InlineData(10_000, 9)]
    public void The_pointer_picks_the_tick_under_it_clamped_to_the_rail(double y, int expected)
    {
        Assert.Equal(expected, MessageRailPolicy.TickAt(y, 10, 10));
    }

    [Fact]
    public void Ticks_rest_at_one_length_and_only_lift_around_the_hovered_one()
    {
        Assert.All(new[] { 0, 3, 9 }, tick => Assert.Equal(1.0, MessageRailPolicy.TickScale(tick, hoverTick: -1, currentTick: -1)));
        Assert.True(MessageRailPolicy.TickScale(5, -1, currentTick: 5) > 1.0);

        var lifts = Enumerable.Range(0, 10).Select(tick => MessageRailPolicy.TickScale(tick, hoverTick: 5, currentTick: -1)).ToArray();
        Assert.Equal(lifts.Max(), lifts[5]);
        Assert.True(lifts[5] > lifts[4] && lifts[4] > lifts[3] && lifts[3] > lifts[2]);
        Assert.Equal(lifts[4], lifts[6]);
        Assert.Equal(1.0, lifts[2]);
        Assert.Equal(1.0, lifts[8]);
    }

    [Theory]
    [InlineData(1280, 6, true)]
    [InlineData(899, 6, false)]
    [InlineData(1280, 1, false)]
    public void The_rail_shows_only_with_a_gutter_and_more_than_one_message(double width, int messages, bool expected)
    {
        Assert.Equal(expected, MessageRailPolicy.HasRoom(width, 820, messages));
    }

    [Fact]
    public void The_preview_drops_markdown_and_splits_title_from_body()
    {
        var preview = MessageRailPolicy.Preview(
            "**Benchmark on the real GUI:** plan first\n\n- The trace showed `two` bugs\n- See [the log](https://example.test/log)",
            isUser: false);

        Assert.Equal("Benchmark on the real GUI: plan first", preview.Title);
        Assert.Equal("The trace showed two bugs See the log", preview.Body);
    }

    [Fact]
    public void Headings_and_snake_case_survive_emphasis_stripping()
    {
        var preview = MessageRailPolicy.Preview("## Fix edit_file\nCall *edit_file* with old_str", isUser: false);
        Assert.Equal("Fix edit_file", preview.Title);
        Assert.Equal("Call edit_file with old_str", preview.Body);
    }

    [Fact]
    public void Long_text_is_trimmed_and_an_empty_reply_reads_as_work_in_progress()
    {
        var preview = MessageRailPolicy.Preview(new string('a', 500) + "\n" + new string('b', 900), isUser: true);
        Assert.Equal(MessageRailPolicy.TitleMaxLength, preview.Title.Length);
        Assert.EndsWith("…", preview.Title);
        Assert.Equal(MessageRailPolicy.BodyMaxLength, preview.Body.Length);

        Assert.Equal("Working…", MessageRailPolicy.Preview("", isUser: false).Title);
        Assert.Equal("Message", MessageRailPolicy.Preview(null, isUser: true).Title);
    }

    [Fact]
    public void The_rail_sits_outside_the_scrolling_list()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var rail = GuiSource.Require(document, "MessageRail");
        var list = GuiSource.Require(document, "ChatListView");
        Assert.Same(list.Parent, rail.Parent);
        Assert.Equal("ChatContentGrid", (string?)rail.Parent?.Attribute(GuiSource.Xaml + "Name"));
        Assert.Contains("IsHitTestVisible = false", GuiSource.Text("Controls", "MessageRailControl.cs"));
    }
}
