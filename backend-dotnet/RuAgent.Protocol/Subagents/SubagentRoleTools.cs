namespace RuAgent.Protocol.Subagents;

/// <summary>Role eligibility only; advertised tools still require Broker policy and run-bound authorization.</summary>
public static class SubagentRoleTools
{
    public static bool Includes(SubagentRole role, string toolId) => IncludesAdapter(role, CodingToolArguments.AdapterToolId(toolId) ?? toolId);

    private static bool IncludesAdapter(SubagentRole role, string toolId) => role switch
    {
        SubagentRole.Review => toolId is "read" or "list" or "help" or "grep" or "glob"
            or "web_search" or "web_fetch" or "skill" or "todo_write",
        SubagentRole.Coding => toolId is "read" or "list" or "help" or "grep" or "glob"
            or "web_search" or "web_fetch" or "skill" or "todo_write" or "write" or "edit" or "bash" or "apply_patch",
        _ => false,
    };
}
