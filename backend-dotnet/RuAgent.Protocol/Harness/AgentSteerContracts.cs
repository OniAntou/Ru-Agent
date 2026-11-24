using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Harness;

/// <summary>
/// Wire contract of a steer (ADR 0056): a message the user sends into a working root run,
/// through <c>POST /agent/v1/runs/{runId}/steers</c>. Shared by the Host, Client, TUI and GUI.
/// </summary>
public static class AgentSteerLimits
{
    /// <summary>
    /// Characters of one steer: the same bound as a run prompt, so anything a turn can carry
    /// (typed text with inlined files) can steer. The delivered activity row carries it whole.
    /// </summary>
    public const int MaxCharacters = AgentRunLimits.MaxPromptChars;

    /// <summary>Steers waiting at once for one run.</summary>
    public const int MaxPending = 8;

    /// <summary>
    /// Characters of all steers waiting for one run together. It keeps the steer list, which
    /// carries each waiting steer's text, well under a client's 8 MiB response bound.
    /// </summary>
    public const int MaxPendingCharacters = 1_000_000;

    /// <summary>Images one steer may carry; the same per-message bound as a run.</summary>
    public const int MaxImages = AgentRunLimits.MaxImages;

    /// <summary>Delivered activity detail: the steer and its image note.</summary>
    public const int MaxDeliveredCharacters = MaxCharacters + 256;

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

/// <summary>
/// One steer of a run. <see cref="Text"/> is empty once the run has read it (the transcript
/// holds it), so the list stays bounded however many steers a long run takes.
/// </summary>
public sealed record AgentSteerItem(
    [property: JsonPropertyName("steer_id")] string SteerId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("created_at")] string CreatedAt,
    [property: JsonPropertyName("delivered_payload_id")] string DeliveredPayloadId,
    [property: JsonPropertyName("image_count")] int ImageCount = 0);

/// <summary>
/// The text a steer with images is delivered as. The images travel with the run's images,
/// which every later request carries; a closing note in the steer's own text tells the model
/// they came with this message, and display surfaces read the count back from it.
/// </summary>
public static partial class AgentSteerText
{
    public static string WithImages(string text, int imageCount)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (imageCount <= 0) return text;
        var note = $"[Images attached to this message: {imageCount}. They are sent with this request's images.]";
        return string.IsNullOrWhiteSpace(text) ? note : text.TrimEnd() + "\n\n" + note;
    }

    /// <summary>The steer as the user wrote it and how many images it carried.</summary>
    public static (string Text, int ImageCount) Split(string? delivered)
    {
        if (string.IsNullOrEmpty(delivered)) return (string.Empty, 0);
        var match = NoteAtEnd().Match(delivered);
        if (!match.Success || !int.TryParse(match.Groups["count"].Value, out var count) || count <= 0)
            return (delivered, 0);
        return (delivered[..match.Index].TrimEnd(), count);
    }

    [GeneratedRegex(@"(?:^|\n)\[Images attached to this message: (?<count>\d{1,3})\. They are sent with this request's images\.\]\s*\z",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 250)]
    private static partial Regex NoteAtEnd();
}

public sealed record AgentSteerListResponse(
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("steers")] IReadOnlyList<AgentSteerItem> Steers);
