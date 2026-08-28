using System.Text.Json.Serialization;
using RuAgent.Protocol;

namespace RuBackend.Conversation;

public sealed record ChatEvent
{
    private const int MaximumScopes = 16;
    private const int MaximumScopeLength = 64;

    public ChatEvent(
        string Type,
        string? Content = null,
        string? SessionId = null,
        string? ErrorCode = null,
        string? Status = null,
        string? ApprovalId = null,
        string? ToolId = null,
        string? Scope = null,
        DateTimeOffset? ExpiresAt = null,
        IReadOnlyList<string>? Scopes = null,
        string? Preview = null,
        string? CorrelationId = null)
    {
        this.Type = Type;
        this.Content = Content;
        this.SessionId = SessionId;
        this.ErrorCode = ErrorCode;
        this.Status = Status;
        this.ApprovalId = ApprovalId;
        this.ToolId = ToolId;
        this.Scope = Scope;
        this.ExpiresAt = ExpiresAt;
        this.Scopes = ValidateScopes(Scopes);
        this.Preview = Preview;
        this.CorrelationId = CorrelationId;
    }

    [JsonPropertyName("type")]
    public string Type { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonPropertyName("error_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ErrorCode { get; init; }

    [JsonPropertyName("status")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Status { get; init; }

    [JsonPropertyName("approval_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApprovalId { get; init; }

    [JsonPropertyName("tool_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolId { get; init; }

    [JsonPropertyName("scope")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Scope { get; init; }

    [JsonPropertyName("expires_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresAt { get; init; }

    [JsonPropertyName("scopes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Scopes { get; init; }

    [JsonPropertyName(ApprovalPreviewFields.Field)]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Preview { get; init; }

    [JsonPropertyName("correlation_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }

    private static IReadOnlyList<string>? ValidateScopes(IReadOnlyList<string>? scopes)
    {
        if (scopes is null)
        {
            return null;
        }

        if (scopes.Count > MaximumScopes
            || scopes.Any(scope => string.IsNullOrWhiteSpace(scope) || scope.Length > MaximumScopeLength))
        {
            throw new ArgumentException("Agent session scopes are invalid.", nameof(scopes));
        }

        return Array.AsReadOnly(scopes.ToArray());
    }
}
