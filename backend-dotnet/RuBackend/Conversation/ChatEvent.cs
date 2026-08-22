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
    string? ErrorCode = null,
    [property: JsonPropertyName("status")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Status = null,
    [property: JsonPropertyName("approval_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ApprovalId = null,
    [property: JsonPropertyName("tool_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ToolId = null,
    [property: JsonPropertyName("scope")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Scope = null,
    [property: JsonPropertyName("expires_at")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    DateTimeOffset? ExpiresAt = null);
