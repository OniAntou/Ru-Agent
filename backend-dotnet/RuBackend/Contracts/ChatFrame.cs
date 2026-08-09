using System.Text.Json.Serialization;

namespace RuBackend.Contracts;

public sealed record ChatFrame(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("content")] string? Content = null,
    [property: JsonPropertyName("session_id")] string? SessionId = null);
