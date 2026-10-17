using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Privacy;
using CoreActivityKind = RuAgent.Harness.Core.AgentActivityKind;
using CoreActivityState = RuAgent.Harness.Core.AgentActivityState;
using ProtocolActivityKind = RuAgent.Protocol.Harness.AgentActivityKind;
using ProtocolActivityState = RuAgent.Protocol.Harness.AgentActivityState;
using ProtocolActivityWire = RuAgent.Protocol.Harness.AgentActivityWire;
using ProtocolTodoWire = RuAgent.Protocol.Harness.AgentTodoWire;
using RuAgent.Protocol.Harness;

namespace RuBackend.Harness;

/// <summary>
/// Runtime-only adapter from Core activity projections to the connection-local transient
/// SSE hub. Nothing published here is appended to the durable harness event store.
/// </summary>
public sealed class AgentActivityLivePublisher : IAgentActivitySink
{
    private readonly HarnessLiveEventHub _liveEvents;
    private readonly KnownSecretRedactor _secrets;

    public AgentActivityLivePublisher(HarnessLiveEventHub liveEvents, KnownSecretRedactor? secrets = null)
    {
        _liveEvents = liveEvents ?? throw new ArgumentNullException(nameof(liveEvents));
        _secrets = secrets ?? KnownSecretRedactor.Empty;
    }

    public void Publish(RunId runId, AgentActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        // Terminal activities carry the model-supplied command line, which may
        // quote a credential (an Authorization header, an API key in argv). This
        // transient surface is client-visible, so it gets the same secret
        // redaction as streamed assistant text.
        var detail = activity.Detail is { Length: > 0 } raw ? _secrets.Redact(raw) : activity.Detail;
        if (AgentEditPreviewCodec.TryDecode(activity.Detail, out var before, out var after))
        {
            detail = AgentEditPreviewCodec.Encode(_secrets.Redact(before), _secrets.Redact(after));
        }
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
            detail,
            MapTodos(activity.Todos));
        AgentActivityWireValidator.Validate(wire);
        var json = AgentActivityWireCodec.Serialize(wire);
        _liveEvents.TryPublish(runId, "agent.activity", json);
    }

    /// <summary>
    /// Maps the Core task rows onto the wire form.
    /// </summary>
    /// <remarks>
    /// A null list stays null rather than becoming an empty array: "this activity
    /// carries no list" and "the list is empty" are different statements, and the
    /// optional wire field already expresses the first.
    /// </remarks>
    private static IReadOnlyList<ProtocolTodoWire>? MapTodos(IReadOnlyList<AgentTodoRow>? todos) =>
        todos?.Select(todo => new ProtocolTodoWire(todo.Content, todo.Status)).ToArray();

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
        CoreActivityKind.Todo => ProtocolActivityKind.Todo,
        CoreActivityKind.Narration => ProtocolActivityKind.Narration,
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
