using RuAgent.Protocol;

namespace RuBackend.Harness.Prompting;

public readonly record struct AgentToolRuntimeAvailability(
    bool WebSearchEnabled,
    bool WebFetchEnabled);

public static class AgentToolRuntimeProjection
{
    public static IReadOnlyList<AgentToolDefinition> Select(AgentToolRuntimeAvailability availability) =>
        AgentToolDefinitionCatalog.Get()
            .Where(definition => definition.ToolId switch
            {
                "read" => true,
                "write" => true,
                "edit" => true,
                "bash" => true,
                _ => true,
            })
            .ToArray();
}
