using RuAgent.Harness.Core;
using CoreActivityKind = RuAgent.Harness.Core.AgentActivityKind;
using CoreActivityState = RuAgent.Harness.Core.AgentActivityState;
using ProtocolActivityKind = RuAgent.Protocol.Harness.AgentActivityKind;
using ProtocolActivityState = RuAgent.Protocol.Harness.AgentActivityState;
using ProtocolActivityWire = RuAgent.Protocol.Harness.AgentActivityWire;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

/// <summary>
/// Runtime-only adapter from Core activity projections to the connection-local transient
/// SSE hub. Nothing published here is appended to the durable harness event store.
/// </summary>
public sealed class AgentActivityLivePublisher : IAgentActivitySink
{
    private readonly HarnessLiveEventHub _liveEvents;

    public AgentActivityLivePublisher(HarnessLiveEventHub liveEvents)
    {
        _liveEvents = liveEvents ?? throw new ArgumentNullException(nameof(liveEvents));
    }

    public void Publish(RunId runId, AgentActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        var wire = new ProtocolActivityWire(
            activity.ActivityId,
            MapKind(activity.Kind),
            MapState(activity.State),
            activity.FileName,
            activity.LineStart,
            activity.LineEnd,
            activity.Symbol,
            activity.Count,
            activity.Added,
            activity.Removed,
            activity.DurationMs,
            activity.SafeCode,
            activity.ToolDisplay,
            activity.Detail);
        AgentActivityWireValidator.Validate(wire);
        var json = AgentActivityWireCodec.Serialize(wire);
        _liveEvents.TryPublish(runId, "agent.activity", json);
    }

    private static ProtocolActivityKind MapKind(CoreActivityKind kind) => kind switch
    {
        CoreActivityKind.Thinking => ProtocolActivityKind.Thinking,
        CoreActivityKind.Explore => ProtocolActivityKind.Explore,
        CoreActivityKind.Search => ProtocolActivityKind.Search,
        CoreActivityKind.Analyze => ProtocolActivityKind.Analyze,
        CoreActivityKind.Edit => ProtocolActivityKind.Edit,
        CoreActivityKind.Terminal => ProtocolActivityKind.Terminal,
        CoreActivityKind.Compaction => ProtocolActivityKind.Compaction,
        CoreActivityKind.Retry => ProtocolActivityKind.Retry,
        CoreActivityKind.Status => ProtocolActivityKind.Status,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ProtocolActivityState MapState(CoreActivityState state) => state switch
    {
        CoreActivityState.Started => ProtocolActivityState.Started,
        CoreActivityState.Completed => ProtocolActivityState.Completed,
        CoreActivityState.Failed => ProtocolActivityState.Failed,
        CoreActivityState.Waiting => ProtocolActivityState.Waiting,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}
