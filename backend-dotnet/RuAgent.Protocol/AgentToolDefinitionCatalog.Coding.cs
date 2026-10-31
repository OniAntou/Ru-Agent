using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

// Input schemas for the coding and terminal tools. Descriptions, scopes and security columns live in
// Tools/AgentToolDescriptorTable; these builders only shape the arguments.
public static partial class AgentToolDefinitionCatalog
{
    internal static JsonObject RunCodeSchema() => ObjectSchema(["code"], ("code", StringSchema(16 * 1024, 1)), ("timeout_ms", IntegerSchema(1, 120_000)));

    internal static JsonObject ApplyPatchSchema() => ObjectSchema(["patch"], ("patch", StringSchema(4 * 1024 * 1024, 1)));

    internal static JsonObject ReadFileSchema() => ObjectSchema(["path"], ("path", StringSchema()), ("start_line", IntegerSchema(1, 1_000_000)), ("line_count", IntegerSchema(1, 1000)));

    internal static JsonObject WriteFileSchema() => ObjectSchema(["path", "content"], ("path", StringSchema()),
        ("content", WithDescription(StringSchema(4 * 1024 * 1024), "Only the intended file text; preserve newlines with JSON \\n escapes. Other tool arguments belong in sibling JSON properties, not in the file text.")),
        ("overwrite", WithDescription(BooleanSchema(), "JSON boolean beside content. Set true only for deliberate full replacement after reading the existing file.")));

    internal static JsonObject EditFileSchema() => ObjectSchema(["path", "edits"], ("path", StringSchema()), ("edits", new JsonObject
    {
        ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 64,
        ["items"] = ObjectSchema(["old_string", "new_string"],
            ("old_string", WithDescription(StringSchema(4 * 1024 * 1024, 1), "Exact file characters copied from read_file.content, including indentation and line breaks. In this JSON argument, encode a real line break as \\n; do not send HTML entities such as &quot;/&#10;, line-number prefixes, or the surrounding JSON wrapper. No fuzzy matching is performed.")),
            ("new_string", WithDescription(StringSchema(4 * 1024 * 1024), "Replacement text with actual indentation and line breaks. In this JSON argument, encode a real line break as \\n; entities such as &quot; or &#10; are stored literally and never decoded.")),
            ("expected_occurrences", IntegerSchema(1, 1024))),
    }));

    internal static JsonObject FileDeleteSchema() => ObjectSchema(["path"], ("path", StringSchema()));

    internal static JsonObject FileRestoreSchema() => ObjectSchema(["recovery_id"], ("recovery_id", StringSchema(256, 1)));

    internal static JsonObject TerminalShellSchema() => TerminalCommandSchema(true);

    internal static JsonObject TerminalProcessSchema() => TerminalCommandSchema(false);

    internal static JsonObject TerminalReadSchema() => ObjectSchema(["execution_id"], ("execution_id", StringSchema(TerminalLimits.MaxExecutionIdChars, 1)), ("offset", IntegerSchema(0, TerminalLimits.MaxSpoolFileChars)), ("limit", IntegerSchema(1, TerminalLimits.MaxSpoolPageChars)));

    internal static JsonObject TerminalStopSchema() => ObjectSchema(["execution_id"], ("execution_id", StringSchema(TerminalLimits.MaxExecutionIdChars, 1)));

    private static JsonObject TerminalCommandSchema(bool shell)
    {
        (string Name, JsonObject Schema)[] fields =
        [
            .. shell
                ? new[] { ("script", WithDescription(StringSchema(TerminalLimits.MaxScriptChars), "Windows PowerShell 5.1 command text, for example dotnet test. Use run_process for literal argv.")) }
                : new[] { ("executable", WithDescription(StringSchema(TerminalLimits.MaxExecutableChars), "Program name or path, without command arguments.")),
                    ("arguments", WithDescription(ArraySchema(TerminalLimits.MaxArguments, TerminalLimits.MaxArgumentChars), "Literal argv array. Use powershell for pipes, redirection or command text.")) },
            ("working_directory", WithDescription(StringSchema(TerminalLimits.MaxWorkingDirectoryChars), "Optional: omit for the workspace. Relative directories resolve against that workspace.")),
            ("timeout_ms", IntegerSchema(1, TerminalLimits.BackgroundMaxLifetimeMs)),
            ("network_intent", BooleanSchema()),
            ("purpose", WithDescription(EnumSchema(["command", "inspection", "verification", "expected_failure"]), "Workflow intent only; never changes execution permission or the actual outcome. verification declares a required check; a failed or pending required check stays outstanding until the same check passes. Rerun a check after later edits. expected_failure declares a negative test that needs its exact expected_exit_code. inspection is diagnostic, not a check; command is not a declared check.")),
            ("expected_exit_code", WithDescription(IntegerSchema(int.MinValue, int.MaxValue), "Only for purpose=expected_failure: the command must exit with exactly this nonzero signed 32-bit Process.ExitCode (for example -1073741819). Zero is rejected. Any other exit is an outstanding failed check. Denial, cancellation, timeout and unknown effects are not expected success.")),
            ("supersedes", WithDescription(new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = TerminalWorkflowIntent.MaximumSupersededChecks, ["items"] = StringSchema(TerminalWorkflowIntent.MaximumSupersededCallIdChars, 1) }, "Deprecated: accepted but ignored. It never retires, replaces or clears a check, so omit it and rerun the same check instead.")),
            .. TerminalWriteSetProperties(),
            ("background", BooleanSchema()),
        ];
        return ObjectSchema([shell ? "script" : "executable"], fields);
    }
}
