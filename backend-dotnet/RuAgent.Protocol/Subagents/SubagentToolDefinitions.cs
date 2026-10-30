using System.Text.Json.Nodes;
using RuAgent.Protocol.Tools;

namespace RuAgent.Protocol.Subagents;

/// <summary>
/// Root orchestration tools; child role projections exclude delegation. The definitions come from
/// the descriptor table rows whose ids start with <c>agent_</c>; this class owns only their argument schemas.
/// </summary>
public static class SubagentToolDefinitions
{
    public static IReadOnlyList<AgentToolDefinition> Get() => Array.AsReadOnly(
        AgentToolDescriptorTable.Subagent.Select(row => row.ToDefinition()).ToArray());

    internal static JsonObject SpawnSchema() => Object(["role", "task"], ("role", new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("coding", "review") }),
        ("task", Text(SubagentLimits.MaxTaskCharacters)), ("context", Text(SubagentLimits.MaxContextBytes, 0)),
        ("model", Text(256)), ("provider_id", Text(128)));

    internal static JsonObject SendSchema() => Object(["agent_id", "message"], ("agent_id", Text(128)), ("message", Text(SubagentLimits.MaxTaskCharacters)));

    internal static JsonObject WaitSchema() => Object(["agent_ids"], ("agent_ids", new JsonObject { ["type"] = "array", ["items"] = Text(128), ["minItems"] = 1, ["maxItems"] = SubagentLimits.MaxIdentities }),
        ("timeout_ms", Integer(0, SubagentLimits.MaxWaitMilliseconds)));

    internal static JsonObject CancelSchema() => Object(["agent_id"], ("agent_id", Text(128)));

    internal static JsonObject ApplySchema() => Object(["agent_id", "result_version"], ("agent_id", Text(128)), ("result_version", Integer(1, SubagentLimits.MaxAttempts)));

    private static JsonObject Text(int maximum, int minimum = 1) =>
        new() { ["type"] = "string", ["minLength"] = minimum, ["maxLength"] = maximum };

    private static JsonObject Integer(long minimum, long maximum) =>
        new() { ["type"] = "integer", ["minimum"] = minimum, ["maximum"] = maximum };

    private static JsonObject Object(string[] required, params (string Name, JsonObject Schema)[] properties)
    {
        var fields = new JsonObject();
        foreach (var property in properties) fields.Add(property.Name, property.Schema);
        return new JsonObject { ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JsonArray(required.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()), ["properties"] = fields };
    }
}
