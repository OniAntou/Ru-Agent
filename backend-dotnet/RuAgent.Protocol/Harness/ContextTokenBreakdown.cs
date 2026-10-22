using System.Text.Json;
using System.Text.Json.Serialization;

namespace RuAgent.Protocol.Harness;

/// <summary>Metadata-only estimates of the three input partitions of one prepared request.</summary>
public sealed record ContextTokenBreakdown
{
    public const string CurrentEstimator = "request-bytes-v1";
    public const int ImageTokenAllowance = 4096;
    public ContextTokenBreakdown(int systemPrompt, int toolDefinitions, int messages)
    {
        if (systemPrompt < 0 || toolDefinitions < 0 || messages < 0
            || (long)systemPrompt + toolDefinitions + messages > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(messages));
        SystemPrompt = systemPrompt;
        ToolDefinitions = toolDefinitions;
        Messages = messages;
    }

    [JsonPropertyName("system_prompt")] public int SystemPrompt { get; }
    [JsonPropertyName("tool_definitions")] public int ToolDefinitions { get; }
    [JsonPropertyName("messages")] public int Messages { get; }
    [JsonPropertyName("estimator")] public string Estimator => CurrentEstimator;
    [JsonIgnore] public int Total => SystemPrompt + ToolDefinitions + Messages;

    public static ContextTokenBreakdown? ReadOptional(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("context_tokens", out var value)
            && TryRead(value, out var result) ? result : null;

    public static bool TryRead(JsonElement value, out ContextTokenBreakdown? result)
    {
        result = null;
        if (value.ValueKind != JsonValueKind.Object) return false;
        int? system = null, tools = null, messages = null;
        string? estimator = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name)) return false;
            if (property.Name == "estimator")
            {
                if (property.Value.ValueKind != JsonValueKind.String) return false;
                estimator = property.Value.GetString();
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var count) || count < 0) return false;
            switch (property.Name)
            {
                case "system_prompt": system = count; break;
                case "tool_definitions": tools = count; break;
                case "messages": messages = count; break;
                default: return false;
            }
        }
        if (system is null || tools is null || messages is null || estimator != CurrentEstimator
            || (long)system.Value + tools.Value + messages.Value > int.MaxValue) return false;
        result = new(system.Value, tools.Value, messages.Value);
        return true;
    }
}
