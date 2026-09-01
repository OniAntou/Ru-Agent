using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

public sealed record AgentModelToolContract(
    string ToolId,
    string ToolVersion,
    JsonObject InputSchema,
    JsonObject? ExampleArguments)
{
    public string? Hint => ExampleArguments?.ToJsonString();
}

public static class AgentModelToolContractCatalog
{
    public const string CurrentVersion = AgentToolDefinitionCatalog.CurrentVersion;

    public static IReadOnlyList<string> ToolIds { get; } =
        Array.AsReadOnly(AgentToolDefinitionCatalog.Get().Select(item => item.ToolId).ToArray());

    public static IReadOnlySet<string> WebToolIds { get; } =
        new HashSet<string>(["web.search", "web.fetch"], StringComparer.Ordinal);

    public static IReadOnlyList<AgentModelToolContract> Get() =>
        AgentToolDefinitionCatalog.Get().Select(ToContract).ToArray();

    public static AgentModelToolContract Get(string toolId, string toolVersion)
    {
        var definition = AgentToolDefinitionCatalog.Find(toolId, toolVersion)
            ?? throw new KeyNotFoundException("tool_contract_not_found");
        return ToContract(definition);
    }

    public static AgentModelToolContract? Find(string toolId, string toolVersion) =>
        AgentToolDefinitionCatalog.Find(toolId, toolVersion) is { } definition
            ? ToContract(definition)
            : null;

    private static AgentModelToolContract ToContract(AgentToolDefinition definition) =>
        new(definition.ToolId, definition.ToolVersion, definition.InputSchema, definition.ExampleArguments);
}
