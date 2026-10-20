using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence.Privacy;
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
            activity.Kind,
            activity.State,
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

}
