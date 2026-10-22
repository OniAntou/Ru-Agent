using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Prompting;

public readonly record struct AgentToolRuntimeAvailability(
    bool WebSearchEnabled,
    bool WebFetchEnabled,
    SubagentRole? ChildRole = null,
    bool McpEnabled = false);

public static class AgentToolRuntimeProjection
{
    public static IReadOnlyList<AgentToolDefinition> Select(AgentToolRuntimeAvailability availability)
    {
        if (availability.ChildRole is { } role && !Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(availability));
        return AgentToolDefinitionCatalog.Get()
            .Where(definition => availability.ChildRole is not { } childRole || SubagentRoleTools.Includes(childRole, definition.ToolId))
            .Where(definition => definition.ToolId switch
            {
                "read" => true,
                "grep" => true,
                "glob" => true,
                "write" => true,
                "edit" => true,
                "bash" => true,
                "web_search" => availability.WebSearchEnabled,
                "web_fetch" => availability.WebFetchEnabled,
                // Advertised only while the user's ~/.ru/mcp.json declares a server.
                "mcp" => availability.McpEnabled,
                _ => true,
            })
            .ToArray();
    }
}
