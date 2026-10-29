using RuAgent.Protocol;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Prompting;

public readonly record struct AgentToolRuntimeAvailability(
    bool WebSearchEnabled,
    bool WebFetchEnabled,
    SubagentRole? ChildRole = null,
    bool McpEnabled = false,
    string ToolProfile = AgentToolProfiles.Minimal,
    bool ComputerUseEnabled = false,
    bool ProviderWebSearch = false,
    bool LanguageServersEnabled = false);

public static class AgentToolRuntimeProjection
{
    public static IReadOnlyList<AgentToolDefinition> Select(AgentToolRuntimeAvailability availability)
    {
        if (availability.ChildRole is { } role && !Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(availability));
        return AgentToolDefinitionCatalog.Get()
            .Where(definition => AgentToolProfiles.Includes(availability.ToolProfile, definition.ToolId))
            .Where(definition => availability.ChildRole is not { } childRole || SubagentRoleTools.Includes(childRole, definition.ToolId))
            .Where(definition => definition.ToolId switch
            {
                // A run that searches through its provider (ADR 0033) has one search tool:
                // the provider's, which carries the same name.
                "web_search" => availability.WebSearchEnabled && !availability.ProviderWebSearch,
                "web_fetch" => availability.WebFetchEnabled,
                // Advertised only while the user's ~/.ru/mcp.json declares a server.
                "mcp" => availability.McpEnabled,
                "code_intelligence" => availability.LanguageServersEnabled,
                // Advertised only for a run that pinned the opt-in with image input (ADR 0031).
                "computer" => availability.ComputerUseEnabled && availability.ChildRole is null,
                _ => true,
            })
            .ToArray();
    }
}
