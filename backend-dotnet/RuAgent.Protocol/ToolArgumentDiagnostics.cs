using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

/// <summary>Schema-based repair feedback only; never authorizes or rewrites a call.</summary>
public sealed record ToolArgumentIssue(string Field, string Expected, string Received, string Signature)
{
    public string Hint => $"Invalid argument {Field}: expected {Expected}; received {Received}. "
        + "Send the exact schema value without added quotes or whitespace; changing the path/content does not repair this field.";
}

public static class ToolArgumentDiagnostics
{
    private static readonly IReadOnlyDictionary<string, JsonObject> Schemas = AgentToolDefinitionCatalog.Get()
        .ToDictionary(tool => tool.ToolId + "/" + tool.ToolVersion, tool => tool.InputSchema);

    public static ToolArgumentIssue? Find(string toolId, string version, JsonElement arguments)
    {
        if (!Schemas.TryGetValue(toolId + "/" + version, out var schema)
            || arguments.ValueKind != JsonValueKind.Object) return null;
        if (schema["required"] is JsonArray required)
            foreach (var name in required.Select(value => value!.GetValue<string>()))
                if (!arguments.TryGetProperty(name, out _)) return Issue(name, "a required field", "missing");
        foreach (var property in schema["properties"]!.AsObject())
        {
            if (!arguments.TryGetProperty(property.Key, out var actual)) continue;
            var definition = property.Value!.AsObject();
            var type = definition["type"]?.GetValue<string>();
            bool matches = type switch
            {
                "string" => actual.ValueKind == JsonValueKind.String,
                "array" => actual.ValueKind == JsonValueKind.Array,
                "object" => actual.ValueKind == JsonValueKind.Object,
                "boolean" => actual.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => actual.ValueKind == JsonValueKind.Number && actual.TryGetInt64(out _),
                "number" => actual.ValueKind == JsonValueKind.Number,
                _ => true,
            };
            // Type diagnostics never echo arbitrary file content, scripts or paths.
            if (!matches) return Issue(property.Key, type!, actual.ValueKind.ToString());
            if (actual.ValueKind == JsonValueKind.String && definition["minLength"] is JsonValue minimum
                && minimum.TryGetValue<int>(out var minLength) && actual.GetString()!.Length < minLength)
                return Issue(property.Key, $"a string of at least {minLength} characters", "a shorter string");
            if (definition["enum"] is JsonArray choices
                && !choices.Any(choice => choice!.GetValue<string>() == actual.GetString()))
            {
                var received = JsonSerializer.Serialize(actual.GetString());
                // Only short enum values are useful; cap without exposing scripts.
                if (received.Length > 80) received = "a string longer than 80 characters";
                return Issue(property.Key, choices.ToJsonString(), received);
            }
        }
        return null;

        ToolArgumentIssue Issue(string field, string expected, string received) =>
            new(field, expected, received, toolId + "/" + version + ":" + field + ":" + expected + ":" + received);
    }
}
