using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RuBackend.Business.Activity;

public sealed record ActivityEvent(
    [property: JsonPropertyName("ts")] double Ts,
    [property: JsonPropertyName("iso")] string Iso,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("data")] JsonObject Data);

public interface IActivityLogStore
{
    Task AppendAsync(ActivityEvent activity, CancellationToken cancellationToken);

    Task<IReadOnlyList<ActivityEvent>> ReadAsync(
        int limit,
        CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}
