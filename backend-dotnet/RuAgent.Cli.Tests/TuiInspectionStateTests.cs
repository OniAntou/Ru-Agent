using System.Text.Json;
using RuAgent.Cli.Tui;
using RuAgent.Client;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiInspectionStateTests
{
    [Fact]
    public void ActivityUpdatesReplaceTheirRowAndPlanReplacesTheWholeList()
    {
        var state = new TuiInspectionState();
        var activity = Edit("edit_a", "a.cs", 3);
        state.Observe(activity with { State = AgentActivityState.Started });
        state.Observe(activity);
        state.Observe(activity); // Reconnect repeats the final activity.
        Assert.Single(state.Rows("/activity"));
        Assert.Equal("a.cs  +3 −1", Assert.Single(state.Rows("/changes")));
        state.Observe(activity with { ActivityId = "plan_a", Kind = AgentActivityKind.Todo, ToolDisplay = null, Todos = [new("Old task", "pending")] });
        state.Observe(activity with { ActivityId = "plan_b", Kind = AgentActivityKind.Todo, ToolDisplay = null, Todos = [new("New task", "completed")] });
        Assert.Single(state.Rows("/activity"));
        Assert.DoesNotContain(state.Rows("/todos"), row => row.Contains("Old task"));
        Assert.Contains("✓ New task", state.Rows("/todos"));
        state.Clear();
        Assert.DoesNotContain(state.Rows("/changes"), row => row.Contains("a.cs"));
        Assert.DoesNotContain(state.Rows("/todos"), row => row.Contains("New task"));
    }

    [Fact]
    public void ReplayKeepsAnOrdinaryLongAnswerAndStillBoundsOversizedMessages()
    {
        var text = new string('x', 12_000) + "END_OF_ANSWER";
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            session = new { messages = new[] { new { role = "assistant", content = text } } },
        }));
        var replay = ChatSessionReplay.Parse(document.RootElement);
        Assert.Equal(text, Assert.Single(replay.Messages).Text);
        Assert.Equal(0, replay.Shortened);
        using var large = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            session = new { messages = new[] { new { role = "assistant", content = new string('x', 120_000) } } },
        }));
        Assert.Equal(1, ChatSessionReplay.Parse(large.RootElement).Shortened);
    }

    private static AgentActivityModel Edit(string id, string file, int added) =>
        new(id, AgentActivityKind.Edit, AgentActivityState.Completed, file, null, null, null,
            null, added, 1, 100, "succeeded", "Edit", null, null);
}
