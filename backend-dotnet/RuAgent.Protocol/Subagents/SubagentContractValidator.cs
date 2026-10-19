using System.Text;
using System.Text.Json;

namespace RuAgent.Protocol.Subagents;

/// <summary>Wire validation only; ownership, role and policy require Broker authority.</summary>
public static class SubagentContractValidator
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static bool IsControlTool(string toolId) => toolId is "agent_spawn" or "agent_send" or "agent_wait" or "agent_cancel";

    public static SubagentValidationResult Validate(string toolId, JsonElement arguments)
    {
        try
        {
            return ValidateCore(toolId, arguments)
                ? SubagentValidationResult.Valid : SubagentValidationResult.Invalid;
        }
        catch (Exception error) when (error is InvalidOperationException or EncoderFallbackException)
        {
            return SubagentValidationResult.Invalid;
        }
    }

    private static bool ValidateCore(string toolId, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) return false;
        string[] fields = toolId switch
        {
            "agent_spawn" => ["role", "task", "context", "model", "provider_id"],
            "agent_send" => ["agent_id", "message"],
            "agent_wait" => ["agent_ids", "timeout_ms"],
            "agent_cancel" => ["agent_id"],
            "agent_apply" => ["agent_id", "result_version"],
            _ => [],
        };
        if (fields.Length == 0) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in arguments.EnumerateObject())
            if (!fields.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) return false;

        return toolId switch
        {
            "agent_spawn" => Text(arguments, "role", 6, out var role) && role is "coding" or "review"
                && Text(arguments, "task", SubagentLimits.MaxTaskCharacters, out _)
                && (!arguments.TryGetProperty("context", out var context)
                    || context.ValueKind == JsonValueKind.String
                    && StrictUtf8.GetByteCount(context.GetString()!) <= SubagentLimits.MaxContextBytes)
                && (!arguments.TryGetProperty("model", out _) || Text(arguments, "model", 256, out _))
                && (!arguments.TryGetProperty("provider_id", out _) || Text(arguments, "provider_id", 128, out _) && Text(arguments, "model", 256, out _)),
            "agent_send" => AgentId(arguments) && Text(arguments, "message", SubagentLimits.MaxTaskCharacters, out _),
            "agent_wait" => Wait(arguments),
            "agent_cancel" => AgentId(arguments),
            "agent_apply" => AgentId(arguments)
                && arguments.TryGetProperty("result_version", out var version)
                && version.ValueKind == JsonValueKind.Number && version.TryGetInt64(out var number) && number is >= 1 and <= SubagentLimits.MaxAttempts,
            _ => false,
        };
    }

    public static bool IsValidId(string? id) => id is { Length: > 0 and <= 128 }
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static bool AgentId(JsonElement arguments) =>
        Text(arguments, "agent_id", 128, out var id) && IsValidId(id);

    private static bool Text(JsonElement arguments, string key, int maximum, out string? text)
    {
        text = null;
        if (!arguments.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return false;
        text = value.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum) return false;
        _ = StrictUtf8.GetByteCount(text);
        return true;
    }

    private static bool Wait(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("agent_ids", out var ids) || ids.ValueKind != JsonValueKind.Array
            || ids.GetArrayLength() is < 1 or > SubagentLimits.MaxIdentities) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids.EnumerateArray())
        {
            if (id.ValueKind != JsonValueKind.String || !IsValidId(id.GetString()) || !seen.Add(id.GetString()!)) return false;
        }
        return !arguments.TryGetProperty("timeout_ms", out var timeout)
            || timeout.ValueKind == JsonValueKind.Number && timeout.TryGetInt32(out var milliseconds)
                && milliseconds is >= 0 and <= SubagentLimits.MaxWaitMilliseconds;
    }
}
