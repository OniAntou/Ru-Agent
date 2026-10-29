using RuAgent.Cli.Subagents;
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class SubagentLiveTrackerTests
{
    private const string Root = "run_0123456789abcdef0123456789abcdef";
    private const string OtherRoot = "run_fedcba9876543210fedcba9876543210";

    private static AgentSubagentChangeModel Change(string root, params (string Id, string Status)[] agents) =>
        new(root, agents.Select(a => new AgentSubagentState(a.Id, a.Status, 1, false, 0)).ToArray(), 1);

    [Theory]
    [InlineData("running", SubagentPhase.Running)]
    [InlineData("waiting_subagents", SubagentPhase.Running)]
    [InlineData("created", SubagentPhase.Running)]
    [InlineData("unavailable", SubagentPhase.Running)]
    [InlineData("waiting_approval", SubagentPhase.Waiting)]
    [InlineData("waiting_user", SubagentPhase.Waiting)]
    [InlineData("completed", SubagentPhase.Done)]
    [InlineData("failed", SubagentPhase.Failed)]
    [InlineData("interrupted", SubagentPhase.Failed)]
    [InlineData("cancelled", SubagentPhase.Stopped)]
    public void Classify_maps_run_status_to_a_phase(string status, SubagentPhase expected) =>
        Assert.Equal(expected, SubagentLiveTracker.Classify(new AgentSubagentState("a", status, 1, false, 0)));

    [Fact]
    public void Classify_treats_a_canceled_child_as_stopped_whatever_its_attempt_says() =>
        Assert.Equal(SubagentPhase.Stopped, SubagentLiveTracker.Classify(new AgentSubagentState("a", "running", 1, true, 0)));

    [Fact]
    public void A_new_child_then_its_completion_are_announced_once_each()
    {
        var tracker = new SubagentLiveTracker();

        var started = tracker.Apply(Change(Root, ("review-1", "running")));
        var repeated = tracker.Apply(Change(Root, ("review-1", "running")));
        var finished = tracker.Apply(Change(Root, ("review-1", "completed")));
        var again = tracker.Apply(Change(Root, ("review-1", "completed")));

        Assert.Equal(new SubagentTransition("review-1", null, SubagentPhase.Running), Assert.Single(started));
        Assert.Empty(repeated);
        Assert.Equal(new SubagentTransition("review-1", SubagentPhase.Running, SubagentPhase.Done), Assert.Single(finished));
        Assert.Empty(again);
        Assert.Equal(0, tracker.ActiveCount);
    }

    [Fact]
    public void The_first_hint_is_a_baseline_so_finished_children_are_shown_but_not_announced()
    {
        var tracker = new SubagentLiveTracker();

        var transitions = tracker.Apply(Change(Root, ("old", "completed"), ("live", "running")));

        Assert.Equal("live", Assert.Single(transitions).AgentId);
        Assert.Equal(2, tracker.Chips.Count);
        Assert.Equal(1, tracker.ActiveCount);
    }

    [Fact]
    public void A_child_that_appears_later_and_is_already_finished_is_announced()
    {
        var tracker = new SubagentLiveTracker();
        tracker.Apply(Change(Root, ("a", "running")));

        var transitions = tracker.Apply(Change(Root, ("a", "running"), ("b", "completed")));

        Assert.Equal(new SubagentTransition("b", null, SubagentPhase.Done), Assert.Single(transitions));
    }

    [Fact]
    public void A_different_root_starts_a_fresh_baseline()
    {
        var tracker = new SubagentLiveTracker();
        tracker.Apply(Change(Root, ("a", "running")));

        var transitions = tracker.Apply(Change(OtherRoot, ("b", "completed")));

        Assert.Empty(transitions);
        Assert.Equal(["b"], tracker.Chips.Select(chip => chip.AgentId));
    }

    [Fact]
    public void Reset_empties_the_strip()
    {
        var tracker = new SubagentLiveTracker();
        tracker.Apply(Change(Root, ("a", "running")));

        tracker.Reset();

        Assert.True(tracker.IsEmpty);
        Assert.Empty(tracker.Chips);
    }

    [Fact]
    public void Transition_lines_name_the_child_and_point_a_finished_one_at_its_result()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.None);

        Assert.Equal("◐ subagent review-1 started", SubagentLiveTracker.TransitionLine(new("review-1", null, SubagentPhase.Running)));
        Assert.Equal("✓ subagent review-1 finished  ·  /subagents review-1 result", SubagentLiveTracker.TransitionLine(new("review-1", SubagentPhase.Running, SubagentPhase.Done)));
        Assert.Equal("✗ subagent review-1 failed", SubagentLiveTracker.TransitionLine(new("review-1", SubagentPhase.Running, SubagentPhase.Failed)));
        Assert.Equal("◇ subagent review-1 waiting", SubagentLiveTracker.TransitionLine(new("review-1", SubagentPhase.Running, SubagentPhase.Waiting)));
    }

    [Fact]
    public void Fit_lists_active_children_first_and_counts_the_ones_that_do_not_fit()
    {
        var chips = new[]
        {
            new SubagentChip("done-1", SubagentPhase.Done),
            new SubagentChip("live-1", SubagentPhase.Running),
            new SubagentChip("live-2", SubagentPhase.Running),
        };

        var wide = string.Concat(SubagentLiveTracker.Fit(chips, 80).Select(span => span.Text));
        var narrow = string.Concat(SubagentLiveTracker.Fit(chips, 20).Select(span => span.Text));

        Assert.Equal("◐ live-1  ◐ live-2  ✓ done-1", wide);
        Assert.StartsWith("◐ live-1", narrow);
        Assert.EndsWith("+2", narrow);
        Assert.True(TuiText.VisibleWidth(narrow) <= 20);
    }

    [Fact]
    public void Host_generated_ids_shorten_but_the_result_hint_keeps_the_full_id()
    {
        using var _ = TuiPalette.PinLevel(TuiColorLevel.None);
        var id = "agent_893a67bd803a12f38d96893f39197927";

        Assert.Equal("agent·893a67", SubagentLiveTracker.DisplayName(id));
        Assert.Equal("review-1", SubagentLiveTracker.DisplayName("review-1"));
        Assert.Equal("agent_short", SubagentLiveTracker.DisplayName("agent_short"));
        Assert.Equal(
            "✓ subagent agent·893a67 finished  ·  /subagents " + id + " result",
            SubagentLiveTracker.TransitionLine(new(id, SubagentPhase.Running, SubagentPhase.Done)));
        Assert.Equal("◐ agent·893a67", string.Concat(SubagentLiveTracker.Fit([new(id, SubagentPhase.Running)], 40).Select(span => span.Text)));
    }

    [Fact]
    public void Fit_never_exceeds_the_width_and_is_empty_when_there_is_no_room_or_no_child()
    {
        var one = new[] { new SubagentChip("a-very-long-subagent-name-indeed", SubagentPhase.Running) };

        Assert.Empty(SubagentLiveTracker.Fit([], 40));
        Assert.Empty(SubagentLiveTracker.Fit(one, 3));
        Assert.True(TuiText.VisibleWidth(string.Concat(SubagentLiveTracker.Fit(one, 12).Select(span => span.Text))) <= 12);
    }

    [Fact]
    public void Header_keeps_strip_right_aligned_without_workspace_heading()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var header = new TgHeaderView { X = 0, Y = 0, Width = Dim.Fill(), Height = 2 };
        window.Add(header);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            header.SetSubagents([new("review-1", SubagentPhase.Running), new("coder-2", SubagentPhase.Done)]);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            var row = TgSnapshot.Capture(app)[1].TrimEnd();
            Assert.DoesNotContain("D:/proj", TgSnapshot.Capture(app)[0], StringComparison.Ordinal);
            Assert.DoesNotContain("D:/proj", row, StringComparison.Ordinal);
            Assert.EndsWith("◐ review-1  ✓ coder-2", row, StringComparison.Ordinal);

            header.SetSubagents([]);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(800), () =>
        {
            Assert.Equal(string.Empty, TgSnapshot.Capture(app)[1].TrimEnd());
            Assert.DoesNotContain("D:/proj", TgSnapshot.Capture(app)[0], StringComparison.Ordinal);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
