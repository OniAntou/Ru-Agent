using System.Text.Json;
using RuAgent.Harness.Core;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentActivityLivePublisherTests
{
    private static readonly RunId Run = new("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

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

        var frame = await lease.Reader.ReadAsync(CancellationToken.None);
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
}
