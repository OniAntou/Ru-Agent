namespace RuAgent.Protocol.Subagents;

/// <summary>Role eligibility only; advertised tools still require Broker policy and run-bound authorization.</summary>
public static class SubagentRoleTools
{
    public static bool Includes(SubagentRole role, string toolId) => role switch
    {
        SubagentRole.Review => toolId is "read" or "read_file" or "list" or "help" or "grep" or "glob"
            or "web_search" or "web_fetch" or "skill" or "todo_write",
        SubagentRole.Coding => toolId is "read" or "read_file" or "list" or "help" or "grep" or "glob"
            or "web_search" or "web_fetch" or "skill" or "todo_write" or "write_file" or "edit_file" or "file_delete" or "file_restore"
            or "powershell" or "run_process" or "terminal_read" or "terminal_stop" or "apply_patch",
        _ => false,
    };
}
