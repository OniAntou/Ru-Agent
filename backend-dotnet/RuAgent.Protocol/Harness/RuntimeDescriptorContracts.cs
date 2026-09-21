using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Harness;

public sealed record RuntimeDescriptor(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("base_url")] string BaseUrl,
    [property: JsonPropertyName("bearer_token")] string BearerToken,
    [property: JsonPropertyName("pid")] int ProcessId,
    [property: JsonPropertyName("process_start_time")] string ProcessStartTime,
    [property: JsonPropertyName("instance_nonce")] string InstanceNonce,
    [property: JsonPropertyName("created_at")] string CreatedAt)
{
    public const int CurrentProtocolVersion = 1;

    public static readonly IReadOnlySet<string> AllowedFields = new HashSet<string>(
        new[] { "protocol_version", "base_url", "bearer_token", "pid", "process_start_time", "instance_nonce", "created_at" },
        StringComparer.Ordinal);

    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
