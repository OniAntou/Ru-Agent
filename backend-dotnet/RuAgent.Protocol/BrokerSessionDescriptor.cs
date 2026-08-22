using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuAgent.Protocol;

public sealed record BrokerSessionDescriptor(
    [property: JsonPropertyName("base_url")] string BaseUrl,
    [property: JsonPropertyName("auth_token")] string AuthToken,
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("session_root")] string SessionRoot,
    [property: JsonPropertyName("expires_at")] DateTimeOffset ExpiresAt)
{
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };
}
