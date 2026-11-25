using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using System.Collections.ObjectModel;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityFeedPresenterTests
{
    [Fact]
    public void New_activity_is_appended_in_arrival_order_and_replays_update_the_same_item()
    {
        var presenter = new AgentActivityFeedPresenter();
        var rows = new List<AgentActivityItem>();

        var first = presenter.Apply(Activity("first"), rows);
        var second = presenter.Apply(Activity("second"), rows);
        var replay = presenter.Apply(Activity("first") with { State = AgentActivityState.Completed }, rows);

        Assert.Equal(new[] { "first", "second" }, rows.Select(item => item.ActivityId));
        Assert.True(first!.Value.Added);
        Assert.True(second!.Value.Added);
        Assert.False(replay!.Value.Added);
        Assert.Same(first.Value.Item, replay.Value.Item);
        presenter.Apply(Activity("first") with { State = AgentActivityState.Completed }, rows);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Replayed_history_items_are_registered_for_live_updates_without_reordering_rows()
    {
        var presenter = new AgentActivityFeedPresenter();
        var restored = new AgentActivityItem(Activity("restored"));
        var rows = new List<AgentActivityItem> { restored };
        presenter.RegisterExisting(restored);

        var update = presenter.Apply(Activity("restored") with { State = AgentActivityState.Completed }, rows);

        Assert.Single(rows);
        Assert.False(update!.Value.Added);
        Assert.Same(restored, update.Value.Item);
        Assert.Equal(AgentActivityState.Completed, restored.State);
    }

    [Fact]
    public void Replay_registration_seeds_identity_from_message_attached_rows()
    {
        var presenter = new AgentActivityFeedPresenter();
        var restored = new AgentActivityItem(Activity("restored"));
        var messages = new[]
        {
            new ChatMessageItem
            {
                Role = "assistant",
                AgentActivities = new ObservableCollection<AgentActivityItem> { restored },
            },
        };
        var rows = new List<AgentActivityItem> { restored };

        Assert.Null(presenter.RegisterReplayedMessages(messages));
        var update = presenter.Apply(Activity("restored") with { State = AgentActivityState.Completed }, rows);

        Assert.False(update!.Value.Added);
        Assert.Same(restored, update.Value.Item);
    }

    [Fact]
    public void Todo_panel_state_tracks_the_newest_activity_that_supplies_a_plan()
    {
        var presenter = new AgentActivityFeedPresenter();
        var rows = new List<AgentActivityItem>();

        var first = presenter.Apply(Activity("first") with
        {
            Todos = [new AgentTodoWire("First task", "pending")],
        }, rows);
        var unrelated = presenter.Apply(Activity("unrelated"), rows);
        var newest = presenter.Apply(Activity("newest") with
        {
            Todos = [new AgentTodoWire("Latest task", "in_progress")],
        }, rows);

        Assert.Equal("First task", Assert.Single(first!.Value.TodoPanelItems!).Content);
        Assert.Null(unrelated!.Value.TodoPanelItems);
        Assert.Equal("Latest task", Assert.Single(newest!.Value.TodoPanelItems!).Content);
    }

    [Fact]
    public void A_thinking_preview_routes_to_its_activity_row_without_creating_one()
    {
        var presenter = new AgentActivityFeedPresenter();
        var rows = new List<AgentActivityItem>();
        var item = presenter.Apply(Activity("act_preview1"), rows)!.Value.Item;

        Assert.True(presenter.ApplyThinkingPreview("act_preview1", "Investigating"));
        Assert.Equal("Investigating", item.ThinkingPreview);

        Assert.False(presenter.ApplyThinkingPreview("act_unknown", "line"));
        Assert.Single(rows);
    }

    [Fact]
    public void A_settled_row_absorbs_the_preview_without_showing_it()
    {
        var presenter = new AgentActivityFeedPresenter();
        var rows = new List<AgentActivityItem>();
        var item = presenter.Apply(Activity("act_preview2"), rows)!.Value.Item;
        presenter.Apply(Activity("act_preview2") with { State = AgentActivityState.Completed }, rows);

        Assert.True(presenter.ApplyThinkingPreview("act_preview2", "late line"));

        Assert.Equal(string.Empty, item.ThinkingPreview);
    }

    private static AgentActivityModel Activity(string id, AgentActivityState state = AgentActivityState.Started) => new(
        id, AgentActivityKind.Thinking, state, null, null, null, null, null, null, null,
        null, "succeeded", null, null, null);
}
