using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RuBackend.Worker;

public sealed record WorkerAdvertisedTools(
    IReadOnlySet<(string ToolId, string ToolVersion)> Entries)
{
    public bool Contains(string toolId, string toolVersion) =>
        Entries.Contains((toolId, toolVersion));
}

public sealed record WorkerActionOutcome(
    int SchemaVersion,
    string ToolId,
    string ToolVersion,
    string Status,
    string? ReasonCode)
{
    private const int MaximumToolIdLength = 128;
    private const int MaximumToolVersionLength = 64;

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

    public static WorkerActionOutcome? ParseOptional(
        JsonNode? node,
        WorkerAdvertisedTools? advertisedTools) =>
        node is null ? null : Parse(node, advertisedTools);

    public static WorkerActionOutcome Parse(
        JsonNode node,
        WorkerAdvertisedTools? advertisedTools)
    {
        try
        {
            return ParseCore(node, advertisedTools);
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

    private static WorkerActionOutcome ParseCore(
        JsonNode node,
        WorkerAdvertisedTools? advertisedTools)
    {
        if (node is not JsonObject root
            || root.Count != Fields.Count
            || root.Any(pair => !Fields.Contains(pair.Key))
            || root["schema_version"]?.GetValue<int>() != 1)
        {
            throw new WorkerProtocolException("invalid_action_outcome");
        }

        var toolId = ReadToolIdentity(root["tool_id"], MaximumToolIdLength);
        var toolVersion = ReadToolIdentity(root["tool_version"], MaximumToolVersionLength);
        if (advertisedTools is not null && !advertisedTools.Contains(toolId, toolVersion))
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
            toolId,
            toolVersion,
            status,
            reason);
    }

    private static string ReadToolIdentity(JsonNode? node, int maxLength)
    {
        if (node?.GetValue<string>() is not { } value
            || string.IsNullOrWhiteSpace(value)
            || value.Length > maxLength)
        {
            throw new WorkerProtocolException("invalid_action_outcome");
        }

        return value;
    }
}
