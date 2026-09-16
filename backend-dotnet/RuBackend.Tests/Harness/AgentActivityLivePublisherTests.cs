using System.Text.Json;
using RuAgent.Harness.Core;
using RuAgent.Protocol;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentActivityLivePublisherTests
{
    private static readonly RunId Run = new("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    private const string ToolCallId = "call_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string ExecutionId = "exec_eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";

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
    public void Core_and_protocol_activity_kinds_stay_in_sync()
    {
        var core = Enum.GetNames(typeof(RuAgent.Harness.Core.AgentActivityKind));
        var protocol = Enum.GetNames(typeof(RuAgent.Protocol.Harness.AgentActivityKind));
        Assert.Equal(core, protocol);
    }

    [Fact]
    public void Core_and_protocol_activity_states_stay_in_sync()
    {
        var core = Enum.GetNames(typeof(RuAgent.Harness.Core.AgentActivityState));
        var protocol = Enum.GetNames(typeof(RuAgent.Protocol.Harness.AgentActivityState));
        Assert.Equal(core, protocol);
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
