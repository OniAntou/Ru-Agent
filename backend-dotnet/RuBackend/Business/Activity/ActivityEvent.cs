using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RuBackend.Business.Activity;

public sealed record ActivityEvent(
    [property: JsonPropertyName("event_id")][property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EventId,
    [property: JsonPropertyName("ts")] double Ts,
    [property: JsonPropertyName("iso")] string Iso,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("data")] JsonObject Data)
{
    public ActivityEvent(
        double ts,
        string iso,
        string type,
        string? sessionId,
        JsonObject data)
        : this(null, ts, iso, type, sessionId, data)
    {
    }
}

public interface IActivityLogStore
{
    Task AppendAsync(ActivityEvent activity, CancellationToken cancellationToken);

    Task<IReadOnlyList<ActivityEvent>> ReadAsync(
        int limit,
        CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}
