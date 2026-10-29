using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;
using RuAgent.Protocol;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentActivityLivePublisherTests
{
    [Fact]
    public async Task Structured_edit_preview_redacts_decoded_secrets_before_publishing()
    {
        const string secret = "synthetic\\secret\"with-quotes-123";
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(Run);
        var publisher = new AgentActivityLivePublisher(hub,
            new RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor([secret]));
        publisher.Publish(Run, new AgentActivity("act_dddddddddddddddddddddddddddddddd",
            AgentActivityKind.Edit, AgentActivityState.Started,
            Detail: RuAgent.Protocol.Harness.AgentEditPreviewCodec.Encode(@"C:\new\notes.txt", secret),
            SafeCode: "in_progress"));
        var frame = await lease.ActivityReader.ReadAsync(CancellationToken.None);
        using var document = JsonDocument.Parse(frame.Data);
        Assert.True(RuAgent.Protocol.Harness.AgentEditPreviewCodec.TryDecode(
            document.RootElement.GetProperty("detail").GetString(), out var before, out var after));
        Assert.Equal(@"C:\new\notes.txt", before);
        Assert.DoesNotContain(secret, after);
        Assert.Contains(RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Replacement, after);
    }

    [Fact]
    public async Task Web_search_context_uses_the_existing_detail_secret_redaction()
    {
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(Run);
        var publisher = new AgentActivityLivePublisher(hub,
            new RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor(["sk-synthetic-secret-123"]));
        publisher.Publish(Run, new AgentActivity("act_dddddddddddddddddddddddddddddddd",
            AgentActivityKind.Search, AgentActivityState.Started,
            SafeCode: "in_progress", ToolDisplay: "Search", Detail: "giá vàng sk-synthetic-secret-123"));
        var frame = await lease.ActivityReader.ReadAsync(CancellationToken.None);
        Assert.DoesNotContain("sk-synthetic-secret-123", frame.Data);
        Assert.Contains(RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Replacement, frame.Data);
    }
    private static readonly RunId Run = new("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private const string ToolCallId = "call_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string ExecutionId = "exec_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

    [Fact]
    public async Task A_terminal_activity_redacts_credentials_before_the_client_sees_it()
    {
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(Run);
        var publisher = new AgentActivityLivePublisher(
            hub,
            new RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor(["sk-live-secret-123"]));

        publisher.Publish(Run, new AgentActivity(
            "act_dddddddddddddddddddddddddddddddd",
            AgentActivityKind.Terminal,
            AgentActivityState.Completed,
            Detail: "curl -H \"Authorization: Bearer sk-live-secret-123\" https://example.test",
            SafeCode: "succeeded"));

        var frame = await lease.ActivityReader.ReadAsync(CancellationToken.None);

        Assert.DoesNotContain("sk-live-secret-123", frame.Data, StringComparison.Ordinal);
        Assert.Contains(
            RuAgent.Harness.Persistence.Privacy.KnownSecretRedactor.Replacement,
            frame.Data,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_todo_activity_carries_its_whole_list_to_the_client()
    {
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(Run);
        var publisher = new AgentActivityLivePublisher(hub);
        var activity = AgentActivityProjector.ProjectTodos(
            "act_todo_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            [
                new AgentTodoRow("Read the catalog", "completed"),
                new AgentTodoRow("Add the tool", "in_progress"),
                new AgentTodoRow("Write the docs", "pending"),
            ]);

        publisher.Publish(Run, activity);

        var frame = await lease.ActivityReader.ReadAsync(CancellationToken.None);
        var parsed = RuAgent.Protocol.Harness.AgentActivityWireCodec.Parse(frame.Data);

        Assert.Equal(RuAgent.Protocol.Harness.AgentActivityKind.Todo, parsed.Kind);
        Assert.NotNull(parsed.Todos);

        // The whole list travels, in order: a viewer that missed an earlier activity
        // still draws the truth from the latest one.
        Assert.Equal(3, parsed.Todos!.Count);
        Assert.Equal("Read the catalog", parsed.Todos[0].Content);
        Assert.Equal("completed", parsed.Todos[0].Status);
        Assert.Equal("in_progress", parsed.Todos[1].Status);
        Assert.Equal("pending", parsed.Todos[2].Status);
    }

    [Fact]
    public async Task An_activity_without_a_list_carries_no_todos()
    {
        // "No list" and "an empty list" are different statements, and only the first is
        // true of an edit activity.
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(Run);
        var publisher = new AgentActivityLivePublisher(hub);

        publisher.Publish(Run, new AgentActivity(
            "act_cccccccccccccccccccccccccccccccc",
            AgentActivityKind.Edit,
            AgentActivityState.Completed,
            "ChatView.xaml",
            SafeCode: "succeeded"));

        var frame = await lease.ActivityReader.ReadAsync(CancellationToken.None);

        Assert.Null(RuAgent.Protocol.Harness.AgentActivityWireCodec.Parse(frame.Data).Todos);
    }

    [Fact]
    public async Task Publish_maps_safe_core_activity_to_agent_activity_transient_frame()
    {
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(Run);
        var publisher = new AgentActivityLivePublisher(hub);
        var activity = new AgentActivity(
            "act_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            AgentActivityKind.Edit,
            AgentActivityState.Completed,
            "ChatView.xaml",
            10,
            12,
            Added: 2,
            Removed: 1,
            SafeCode: "succeeded");

        publisher.Publish(Run, activity);

        var frame = await lease.ActivityReader.ReadAsync(CancellationToken.None);
        Assert.Equal("agent.activity", frame.Kind);
        using var document = JsonDocument.Parse(frame.Data);
        var parsed = RuAgent.Protocol.Harness.AgentActivityWireCodec.Parse(frame.Data);
        Assert.Equal("edit", document.RootElement.GetProperty("kind").GetString());
        Assert.Equal(AgentActivityState.Completed.ToString(), parsed.State.ToString());
        Assert.Equal("ChatView.xaml", parsed.FileName);
        Assert.Equal(2, parsed.Added);
        Assert.Equal(1, parsed.Removed);
        Assert.DoesNotContain("command", frame.Data, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diff", frame.Data, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("output", frame.Data, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Publish_forwards_tool_display_to_transient_frame()
    {
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(Run);
        var publisher = new AgentActivityLivePublisher(hub);
        var activity = new AgentActivity(
            "act_dddddddddddddddddddddddddddddddd",
            AgentActivityKind.Analyze,
            AgentActivityState.Completed,
            SafeCode: "succeeded",
            ToolDisplay: "View");

        publisher.Publish(Run, activity);

        var frame = await lease.ActivityReader.ReadAsync(CancellationToken.None);
        Assert.Equal("agent.activity", frame.Kind);
        var parsed = RuAgent.Protocol.Harness.AgentActivityWireCodec.Parse(frame.Data);
        Assert.Equal("View", parsed.ToolDisplay);
    }

    [Fact]
    public async Task Activity_frame_survives_a_delta_burst_that_overflows_the_delta_channel()
    {
        var hub = new HarnessLiveEventHub();
        await using var lease = hub.Subscribe(Run);
        var publisher = new AgentActivityLivePublisher(hub);
        var activity = new AgentActivity(
            "act_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            AgentActivityKind.Analyze,
            AgentActivityState.Started,
            SafeCode: "in_progress",
            ToolDisplay: "View");

        publisher.Publish(Run, activity);

        for (var i = 1; i <= 200; i++)
        {
            hub.TryPublishDelta(new RuAgent.Protocol.Harness.BrokerToolOutputDeltaWire(
                Run.Value,
                ToolCallId,
                ExecutionId,
                i,
                "stdout",
                $"chunk-{i}",
                Redacted: false));
        }

        Assert.True(hub.DroppedCount > 0);
        Assert.Equal(0, hub.ActivityDroppedCount);

        var frame = await lease.ActivityReader.ReadAsync(CancellationToken.None);
        Assert.Equal("agent.activity", frame.Kind);
        var parsed = RuAgent.Protocol.Harness.AgentActivityWireCodec.Parse(frame.Data);
        Assert.Equal("act_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", parsed.ActivityId);
        Assert.Equal("View", parsed.ToolDisplay);
    }

    [Fact]
    public void Invalid_activity_is_dropped_by_failure_isolated_core_publish()
    {
        var hub = new HarnessLiveEventHub();
        var publisher = new AgentActivityLivePublisher(hub);
        var invalid = new AgentActivity(
            "act_cccccccccccccccccccccccccccccccc",
            AgentActivityKind.Analyze,
            AgentActivityState.Completed,
            "dir/file.cs",
            SafeCode: "succeeded");

        Exception? exception = null;
        try
        {
            AgentActivityProjector.PublishSafe(publisher, Run, invalid);
        }
        catch (Exception error)
        {
            exception = error;
        }

        Assert.Null(exception);
        Assert.Equal(0, hub.DroppedCount);
    }

    [Fact]
    public void Activity_kind_wire_names_keep_their_original_order()
    {
        var protocol = Enum.GetNames(typeof(RuAgent.Protocol.Harness.AgentActivityKind));
        Assert.Equal(
            // New kinds are only ever appended, so stored and in-flight tokens keep their meaning.
            new[] { "Thinking", "Explore", "Search", "Analyze", "Edit", "Terminal", "Compaction", "Retry", "Status", "Todo", "Narration",
                "Web", "Mcp", "Computer", "Skill", "Program" },
            protocol);
    }

    [Fact]
    public void Activity_state_wire_names_keep_their_original_order()
    {
        var protocol = Enum.GetNames(typeof(RuAgent.Protocol.Harness.AgentActivityState));
        Assert.Equal(new[] { "Started", "Completed", "Failed", "Waiting" }, protocol);
    }

    [Fact]
    public void PublishSafe_counts_sink_failures_without_throwing()
    {
        var before = AgentActivityProjector.DroppedPublishCount;
        var activity = new AgentActivity(
            "act_ffffffffffffffffffffffffffffffff",
            AgentActivityKind.Status,
            AgentActivityState.Started,
            SafeCode: "in_progress");

        AgentActivityProjector.PublishSafe(new ThrowingSink(), Run, activity);

        Assert.Equal(before + 1, AgentActivityProjector.DroppedPublishCount);
    }

    private sealed class ThrowingSink : RuAgent.Harness.Core.IAgentActivitySink
    {
        public void Publish(RunId runId, AgentActivity activity) =>
            throw new InvalidOperationException("sink is gone");
    }
}
