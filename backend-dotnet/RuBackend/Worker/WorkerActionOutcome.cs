using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RuBackend.Worker;

public sealed record WorkerActionOutcome(
    int SchemaVersion,
    string ToolId,
    string ToolVersion,
    string Status,
    string? ReasonCode)
{
    private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
    {
        "succeeded",
        "failed",
        "denied",
        "approval_required",
        "cancelled",
        "timed_out",
        "clarification_required",
    };

    private static readonly Regex SafeCode = new(
        "^[a-z0-9][a-z0-9_.-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "schema_version",
        "tool_id",
        "tool_version",
        "status",
        "reason_code",
    };

    public bool ShouldRetain => !string.Equals(
        Status,
        "succeeded",
        StringComparison.Ordinal);

    public JsonObject ToJson() => new()
    {
        ["schema_version"] = SchemaVersion,
        ["tool_id"] = ToolId,
        ["tool_version"] = ToolVersion,
        ["status"] = Status,
        ["reason_code"] = ReasonCode,
    };

    public static WorkerActionOutcome? ParseOptional(JsonNode? node) =>
        node is null ? null : Parse(node);

    public static WorkerActionOutcome Parse(JsonNode node)
    {
        try
        {
            return ParseCore(node);
        }
        catch (WorkerProtocolException)
        {
            throw;
        }
        catch (Exception error) when (
            error is InvalidOperationException or FormatException or OverflowException)
        {
            throw new WorkerProtocolException("invalid_action_outcome");
        }
    }

    private static WorkerActionOutcome ParseCore(JsonNode node)
    {
        if (node is not JsonObject root
            || root.Count != Fields.Count
            || root.Any(pair => !Fields.Contains(pair.Key))
            || root["schema_version"]?.GetValue<int>() != 1
            || root["tool_id"]?.GetValue<string>() != "system.volume"
            || root["tool_version"]?.GetValue<string>() != "1.0.0")
        {
            throw new WorkerProtocolException("invalid_action_outcome");
        }

        var status = root["status"]?.GetValue<string>();
        var reason = root["reason_code"]?.GetValue<string>();
        if (status is null
            || !AllowedStatuses.Contains(status)
            || status == "succeeded" && reason is not null
            || status != "succeeded" && (reason is null || !SafeCode.IsMatch(reason)))
        {
            throw new WorkerProtocolException("invalid_action_outcome");
        }

        return new WorkerActionOutcome(
            1,
            "system.volume",
            "1.0.0",
            status,
            reason);
    }
}
