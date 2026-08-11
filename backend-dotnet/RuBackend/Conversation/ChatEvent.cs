using System.Text.Json.Serialization;

namespace RuBackend.Conversation;

public sealed record ChatEvent(
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("content")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Content = null,
    [property: JsonPropertyName("session_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? SessionId = null,
    [property: JsonPropertyName("error_code")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ErrorCode = null);
