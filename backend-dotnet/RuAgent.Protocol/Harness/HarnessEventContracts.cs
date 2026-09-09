using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Harness;

public sealed record HarnessSafeEventEnvelope(
    [property: JsonPropertyName("protocol_version")] int ProtocolVersion,
    [property: JsonPropertyName("event_id")] string EventId,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("occurred_at")] string OccurredAt,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("safe_summary")] string SafeSummary,
    [property: JsonPropertyName("payload_ref")] string? PayloadRef = null,
    [property: JsonPropertyName("approval_id")] string? ApprovalId = null,
    [property: JsonPropertyName("input_tokens")] long? InputTokens = null,
    [property: JsonPropertyName("output_tokens")] long? OutputTokens = null,
    [property: JsonPropertyName("total_tokens")] long? TotalTokens = null)
{
    public const int CurrentProtocolVersion = 1;

    public static bool IsValidProtocolVersion(int version) => version == CurrentProtocolVersion;
}

public static class HarnessEventContractValidator
{
    private static readonly HashSet<string> AllowedEventTypes = new(StringComparer.Ordinal)
    {
        "run.state_changed",
        "run.failed",
        "model.step_started",
        "model.step_completed",
        "tool.proposed",
        "tool.execution_requested",
        "tool.execution_completed",
        "approval.requested",
        "continue.requested",
        "reconciliation.required",
        "transient.frames_dropped",
    };

    private static readonly HashSet<string> ForbiddenFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "command",
        "arguments",
        "argv",
        "path",
        "code",
        "diff",
        "output",
        "provider",
        "control",
        "prompt",
        "script",
        "stdout",
        "stderr",
        "body",
        "environment",
        "exception_message",
        "exception",
    };

    public static bool IsAllowedEventType(string eventType) => AllowedEventTypes.Contains(eventType);

    public static bool ContainsForbiddenField(string json)
    {
        foreach (var field in ForbiddenFields)
        {
            var pattern = $"\"{field}\"";
            if (json.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
