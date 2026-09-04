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
                "web.search" => availability.WebSearchEnabled,
                "web.fetch" => availability.WebFetchEnabled,
                _ => true,
            })
            .ToArray();
}
