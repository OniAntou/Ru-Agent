using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

public static class AgentToolProfiles
{
    public const string Minimal = "coding_v2";
    public const string Patch = "patch_v2";
    public const string Programmatic = "coding_code_v1";
    public const string PinField = "ru_tool_profile";

    // A versioned profile is an explicit inventory: future catalog additions must
    // not silently appear in an already-pinned run after an application update.
    private static readonly IReadOnlySet<string> Common = new HashSet<string>(
        ["read", "list", "help", "todo_write", "grep", "glob", "web_search", "web_fetch", "skill", "ask_user", "mcp", "computer",
         "agent_spawn", "agent_send", "agent_wait", "agent_cancel", "agent_apply"], StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> Coding = new HashSet<string>(
        ["read_file", "write_file", "edit_file", "file_delete", "file_restore", "powershell", "run_process", "terminal_read", "terminal_stop"], StringComparer.Ordinal);

    public static string ReadPin(JsonObject options)
    {
        if (!options.ContainsKey(PinField)) throw new AgentProtocolException("tool_profile_required");
        if (options[PinField] is JsonValue value && value.TryGetValue<string>(out var profile)
            && profile is Minimal or Patch or Programmatic) return profile;
        throw new AgentProtocolException("tool_profile_invalid");
    }

    // Querying an unsupported historical record must not create executable
    // metadata or invent a replacement pin. Execution always uses ReadPin.
    public static string? ReadSupportedLabel(JsonObject options) =>
        options[PinField] is JsonValue value && value.TryGetValue<string>(out var profile)
            && profile is Minimal or Patch or Programmatic ? profile : null;

    public static bool Includes(string profile, string toolId) => profile switch
    {
        Minimal => Common.Contains(toolId) || Coding.Contains(toolId),
        Programmatic => Common.Contains(toolId) || Coding.Contains(toolId) || toolId is "run_code" or "apply_patch",
        Patch => Common.Contains(toolId) || toolId == "apply_patch"
            || Coding.Contains(toolId) && toolId is not ("write_file" or "edit_file"),
        _ => throw new AgentProtocolException("tool_profile_invalid"),
    };

    public static string ForNewRun(string? configured = null)
    {
        if (configured is not null)
            return ReadPin(new JsonObject { [PinField] = configured });
        // Opt in to patch experiments explicitly; a model-name guess is not quality evidence.
        return Minimal;
    }
}
