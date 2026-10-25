using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

public static partial class AgentToolDefinitionCatalog
{
    private static IEnumerable<AgentToolDefinition> CodingDefinitions(int ordinal)
    {
        Entry[] entries =
        [
            new("run_code", "Run a bounded synchronous JavaScript function body; use return to select JSON for the model. No CLR, filesystem, network, imports, Date, randomness or async/Promise APIs. Call tools.read_file({path:'x'}) or tools.call('grep',{query:'x'}); each returns {status,output,error_code}. SDK tools: read, read_file, list, grep, glob, write_file, edit_file, file_delete, file_restore, apply_patch, powershell, run_process, terminal_read, terminal_stop. Calls execute sequentially with normal policy; approval-required calls stop the program: propose that tool directly. Maximum 32 calls, 16 KiB code, 256 KiB selected result. Interpreter replay uses saved receipts and never repeats uncertain effects. Use direct calls for images or interactive approvals. Do not hide failed checks: verification uses all nested receipts.",
                AgentToolScopes.SystemRead, () => ObjectSchema(["code"], ("code", StringSchema(16 * 1024, 1)), ("timeout_ms", IntegerSchema(1, 120_000)))),
            new("apply_patch", "Apply a bounded unified diff to workspace files. Use --- a/path and +++ b/path headers with exact @@ -old,count +new,count @@ hunks. /dev/null denotes file creation/deletion. All paths are checked before mutation; no renames, binary patches, fuzzy hunks or outside-workspace paths. A rejected hunk leaves files unchanged; after an I/O failure inspect current files before retrying. Send only patch; hashes are captured by Broker.",
                AgentToolScopes.WorkspaceWrite, () => ObjectSchema(["patch"], ("patch", StringSchema(4 * 1024 * 1024, 1)))),
            new("read_file", "Read UTF-8 text by 1-based start_line and line_count (default 200). Continue at next_line when has_more=true. Lines retain original newlines. Snapshot scan is limited to 4 MiB. For images, very large files or read_line_too_long, use read with byte paging; never guess byte positions inside Unicode characters." + FilePathGuidance,
                AgentToolScopes.SystemRead, () => ObjectSchema(["path"], ("path", StringSchema()), ("start_line", IntegerSchema(1, 1_000_000)), ("line_count", IntegerSchema(1, 1000)))),
            new("write_file", "Create a new UTF-8 file using path and content. Existing files require overwrite=true; read them first. Use edit_file for partial changes." + FilePathGuidance,
                AgentToolScopes.WorkspaceWrite, () => ObjectSchema(["path", "content"], ("path", StringSchema()), ("content", StringSchema(4 * 1024 * 1024)), ("overwrite", BooleanSchema()))),
            new("edit_file", "Atomically edit one existing file. edits is a list of exact old_string/new_string replacements, all matched against the ORIGINAL file. Use non-empty, unique old_string copied from a fresh read; expected_occurrences explicitly permits replacing repeated text. Overlapping edits fail without changes. Never use this to create a file." + FilePathGuidance,
                AgentToolScopes.WorkspaceWrite, () => ObjectSchema(["path", "edits"], ("path", StringSchema()), ("edits", new JsonObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 64,
                    ["items"] = ObjectSchema(["old_string", "new_string"], ("old_string", StringSchema(4 * 1024 * 1024, 1)), ("new_string", StringSchema(4 * 1024 * 1024)), ("expected_occurrences", IntegerSchema(1, 1024))),
                }))),
            new("file_delete", "Delete one workspace file, retaining the existing recovery behavior. This is a mutation subject to session policy." + FilePathGuidance,
                AgentToolScopes.WorkspaceWrite, () => ObjectSchema(["path"], ("path", StringSchema()))),
            new("file_restore", "Restore a deleted file by the recovery_id returned by file_delete. Never invent an ID.",
                AgentToolScopes.WorkspaceWrite, () => ObjectSchema(["recovery_id"], ("recovery_id", StringSchema(256, 1)))),
            new("powershell", "Execute Windows PowerShell 5.1 command text in script. Use PowerShell syntax; curl is an alias, use curl.exe for the executable. Use for builds, tests and git. Omit working_directory for the workspace. background=true starts a bounded task; use terminal_read and terminal_stop with its execution_id. Programs run with the current user's rights. purpose=verification declares a required check; inspection failures remain diagnostic.",
                AgentToolScopes.TerminalExecute, () => TerminalCommandSchema(true)),
            new("run_process", "Launch an executable with literal arguments (argv), without shell parsing. Use powershell for pipes or redirection. background=true starts a bounded task; retain execution_id for terminal_read/terminal_stop. Omit working_directory for the workspace. Programs run with the current user's rights.",
                AgentToolScopes.TerminalExecute, () => TerminalCommandSchema(false)),
            new("terminal_read", "Read retained output of a terminal execution without rerunning it. offset counts UTF-16 characters; continue at the returned cursor or projection_next_offset. This does not accept stdin.",
                AgentToolScopes.TerminalExecute, () => ObjectSchema(["execution_id"], ("execution_id", StringSchema(TerminalLimits.MaxExecutionIdChars, 1)), ("offset", IntegerSchema(0, TerminalLimits.MaxSpoolFileChars)), ("limit", IntegerSchema(1, TerminalLimits.MaxSpoolPageChars)))),
            new("terminal_stop", "Stop a background terminal task belonging to this session by its execution_id.",
                AgentToolScopes.TerminalExecute, () => ObjectSchema(["execution_id"], ("execution_id", StringSchema(TerminalLimits.MaxExecutionIdChars, 1)))),
        ];
        foreach (var entry in entries)
            yield return new AgentToolDefinition(ordinal++, entry.Id, CurrentVersion, entry.Description, entry.Scope, entry.Schema, entry.Example);
    }

    private static JsonObject TerminalCommandSchema(bool shell)
    {
        var fields = BashSchemaProperties().Where(field => field.Name is not ("mode" or "shell" or "execution_id" or "offset" or "limit")
            && (shell ? field.Name is not ("executable" or "arguments") : field.Name != "script")).ToArray();
        return ObjectSchema([shell ? "script" : "executable"], fields);
    }
}
