using System.Xml.Linq;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Controls;
using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The live feed's motion and clocks: a running row's clock, a running command's output
/// line, a fresh row's entrance and a settle's count-up. Each one belongs only to a row the
/// feed watched happen; replayed or adopted rows must stay still and show no invented time.
/// </summary>
public sealed class AgentActivityLiveMotionTests
{
    [Fact]
    public void A_row_created_running_by_the_live_feed_has_a_clock_that_stops_when_it_settles()
    {
        var presenter = new AgentActivityFeedPresenter();
        var rows = new List<AgentActivityItem>();

        var item = presenter.Apply(Activity("cmd"), rows)!.Value.Item;

        Assert.True(item.HasLiveClock);
        Assert.InRange(item.LiveElapsedAt(Environment.TickCount64 + 5_000)!.Value.TotalMilliseconds, 5_000, 6_000);

        presenter.Apply(Activity("cmd", AgentActivityState.Completed), rows);

        Assert.False(item.HasLiveClock);
        Assert.Null(item.LiveElapsedAt(Environment.TickCount64));
    }

    [Fact]
    public void A_replayed_row_still_running_gets_no_clock_from_a_later_live_frame()
    {
        // Adopted mid-run: the start happened before this transcript watched, so any
        // clock would understate the time.
        var presenter = new AgentActivityFeedPresenter();
        var restored = new AgentActivityItem(Activity("cmd"));
        var rows = new List<AgentActivityItem> { restored };
        presenter.RegisterExisting(restored);

        presenter.Apply(Activity("cmd") with { Detail = "dotnet test" }, rows);

        Assert.True(restored.IsRunning);
        Assert.False(restored.HasLiveClock);
        Assert.False(restored.TryTakeEntrance(Environment.TickCount64));
    }

    [Fact]
    public void A_row_that_starts_running_after_waiting_starts_its_clock_then()
    {
        var item = new AgentActivityItem(Activity("cmd", AgentActivityState.Waiting));
        item.ObserveLiveFrame(created: true, wasRunning: false, nowTick: 1_000);
        Assert.False(item.HasLiveClock);

        item.UpdateFrom(Activity("cmd"));
        item.ObserveLiveFrame(created: false, wasRunning: false, nowTick: 4_000);

        Assert.Equal(TimeSpan.FromSeconds(3), item.LiveElapsedAt(7_000));
    }

    [Fact]
    public void The_clock_shows_only_after_a_full_second()
    {
        var item = new AgentActivityItem(Activity("cmd"));
        item.ObserveLiveFrame(created: true, wasRunning: false, nowTick: 10_000);

        Assert.Null(ActivityRowTail.LiveClockText(item, 10_999));
        Assert.Equal("1s", ActivityRowTail.LiveClockText(item, 11_000));
        Assert.Equal("1m 05s", ActivityRowTail.LiveClockText(item, 75_000));
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59_900, "59s")]
    [InlineData(60_000, "1m 00s")]
    [InlineData(3_725_000, "62m 05s")]
    public void Live_elapsed_reads_as_seconds_then_minutes(long milliseconds, string expected) =>
        Assert.Equal(expected, AgentActivityItem.FormatLiveElapsed(TimeSpan.FromMilliseconds(milliseconds)));

    [Fact]
    public void Entrance_is_taken_once_and_only_inside_its_window()
    {
        var item = new AgentActivityItem(Activity("cmd"));
        item.ObserveLiveFrame(created: true, wasRunning: false, nowTick: 1_000);
        Assert.True(item.TryTakeEntrance(1_000 + AgentActivityItem.EntranceWindowMilliseconds));
        Assert.False(item.TryTakeEntrance(1_001));

        var late = new AgentActivityItem(Activity("late"));
        late.ObserveLiveFrame(created: true, wasRunning: false, nowTick: 1_000);
        Assert.False(late.TryTakeEntrance(1_001 + AgentActivityItem.EntranceWindowMilliseconds));
    }

    [Fact]
    public void Replay_never_marks_an_entrance()
    {
        var replayed = new AgentActivityItem(Activity("old", AgentActivityState.Completed));

        Assert.False(replayed.TryTakeEntrance(Environment.TickCount64));
    }

    [Theory]
    [InlineData("build\nPassed! 137\n", "Passed! 137")]
    [InlineData("one\n\n  \n", "one")]
    [InlineData("progress 10%\rprogress 55%\rprogress 90%", "progress 90%")]
    [InlineData("tab\there\u0007", "tab here")]
    [InlineData("", "")]
    [InlineData("\n\n", "")]
    public void The_ticker_shows_the_newest_non_empty_output_line(string output, string expected) =>
        Assert.Equal(expected, AgentActivityItem.LastOutputLine(output));

    [Fact]
    public void A_long_output_line_is_cut_without_splitting_a_surrogate_pair()
    {
        var line = new string('a', 199) + "🙂" + new string('b', 50);

        var shown = AgentActivityItem.LastOutputLine(line);

        Assert.EndsWith("…", shown, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(shown[^2]));
        Assert.True(shown.Length <= 201);
    }

    [Fact]
    public void The_ticker_follows_a_running_closed_command_only()
    {
        var item = new AgentActivityItem(Activity("cmd") with { Kind = AgentActivityKind.Terminal, Detail = "dotnet test" });
        item.AppendTerminalOutput("Restoring\nBuilding\n");
        Assert.Equal("Building", item.TerminalTickerText);

        item.IsEditPreviewExpanded = true;
        Assert.Equal(string.Empty, item.TerminalTickerText);

        item.IsEditPreviewExpanded = false;
        item.UpdateFrom(Activity("cmd", AgentActivityState.Completed) with { Kind = AgentActivityKind.Terminal });
        Assert.Equal(string.Empty, item.TerminalTickerText);
    }

    [Fact]
    public void The_ticker_sits_under_the_header_in_the_activity_template()
    {
        var template = GuiSource.Xml("Views", "ChatView.xaml").Descendants()
            .First(element => element.Attribute(GuiSource.Xaml + "Key")?.Value == "AgentActivityTemplate");
        var ticker = GuiSource.Require(template.Document!, "ActivityTicker");

        Assert.Equal("ActivityRowTicker", ticker.Name.LocalName);
        Assert.Equal("1", GuiSource.AttributeValue(ticker, "Grid.Row"));
        Assert.False(GuiSource.IsDescendantOf(ticker, GuiSource.Require(template.Document!, "ActivityHeader")));
    }

    [Fact]
    public void Only_the_live_feed_marks_rows_as_watched()
    {
        // Replay builds rows straight from the model; it must never start clocks or entrances.
        var replayLoader = GuiSource.Text("Services", "AgentActivityReplayLoader.cs");
        Assert.DoesNotContain("ObserveLiveFrame", replayLoader, StringComparison.Ordinal);
        var presenter = GuiSource.Text("Services", "AgentActivityFeedPresenter.cs");
        Assert.Contains("ObserveLiveFrame(created: true", presenter, StringComparison.Ordinal);
    }

    private static AgentActivityModel Activity(string id, AgentActivityState state = AgentActivityState.Started) => new(
        id, AgentActivityKind.Terminal, state, null, null, null, null, null, null, null,
        null, "succeeded", null, null, null);
}
