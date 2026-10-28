namespace RuAgent.Protocol.Programs;

public static class ProgramToolContract
{
    public const string Revision = "coding_code_v1.jint-4.16.3.2";
    public const int MaximumParallelCalls = 8;
    public static readonly IReadOnlySet<string> SupportedTools = new HashSet<string>(
        ["read", "read_file", "list", "grep", "glob", "write_file", "edit_file", "file_delete", "file_restore",
         "apply_patch", "powershell", "run_process", "terminal_read", "terminal_stop"], StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> ParallelTools = new HashSet<string>(
        ["read", "read_file", "list", "grep", "glob"], StringComparer.Ordinal);
    public static bool CanRunInParallel(string tool) => ParallelTools.Contains(tool);
    public static bool CanHaveEffects(string tool) => tool is "write_file" or "edit_file" or "file_delete" or "file_restore"
        or "apply_patch" or "powershell" or "run_process" or "terminal_stop";
}
