using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Harness;

/// <summary>
/// Wire contract of a steer (ADR 0056): a message the user sends into a working root run,
/// through <c>POST /agent/v1/runs/{runId}/steers</c>. Shared by the Host, Client, TUI and GUI.
/// </summary>
public static class AgentSteerLimits
{
    /// <summary>A steer is guidance, not a new prompt; the delivered activity row carries it whole.</summary>
    public const int MaxCharacters = 4_000;

    /// <summary>Steers waiting at once for one run.</summary>
    public const int MaxPending = 8;

    public const string IdPrefix = "steer_";

    /// <summary>Safe code (<c>safe_summary</c> on the wire) of the <c>run.note_added</c> event that delivers a steer.</summary>
    public const string DeliveredSafeCode = "user_steer";

    public const string StatePending = "pending";
    public const string StateDelivered = "delivered";

    /// <summary>The run ended before the steer reached a step boundary; offer it again as a new turn.</summary>
    public const string StateReturned = "returned";

    public static string NewSteerId() => IdPrefix + Guid.NewGuid().ToString("N");

    public static bool IsValidSteerId(string? steerId) =>
        steerId is { Length: 38 }
        && steerId.StartsWith(IdPrefix, StringComparison.Ordinal)
        && steerId.AsSpan(IdPrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    /// <summary>The payload that delivers the steer shares its hex, so the sender can match the event.</summary>
    public static string DeliveredPayloadId(string steerId) =>
        IsValidSteerId(steerId)
            ? "payload_" + steerId[IdPrefix.Length..]
            : throw new ArgumentException("steer_id_invalid", nameof(steerId));
}

public sealed record AgentSteerResponse(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("steer_id")] string SteerId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("delivered_payload_id")] string DeliveredPayloadId);

public sealed record AgentSteerItem(
    [property: JsonPropertyName("steer_id")] string SteerId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("delivered_payload_id")] string DeliveredPayloadId);

public sealed record AgentSteerListResponse(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("steers")] IReadOnlyList<AgentSteerItem> Steers);
