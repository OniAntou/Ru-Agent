using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

public static class AgentToolProfiles
{
    /// <summary>Earlier default, kept so runs already pinned to it continue.</summary>
    public const string Minimal = "coding_v2";
    /// <summary>
    /// The single default since ADR 0043: coding_v2 plus apply_patch and run_code.
    /// It replaces the opt-in patch_v2 and coding_code_v1 experiments.
    /// </summary>
    public const string Current = "coding_v3";
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
            && profile is Minimal or Current) return profile;
        throw new AgentProtocolException("tool_profile_invalid");
    }

    // Querying an unsupported historical record must not create executable
    // metadata or invent a replacement pin. Execution always uses ReadPin.
    public static string? ReadSupportedLabel(JsonObject options) =>
        options[PinField] is JsonValue value && value.TryGetValue<string>(out var profile)
            && profile is Minimal or Current ? profile : null;

    public static bool Includes(string profile, string toolId) => profile switch
    {
        Minimal => Common.Contains(toolId) || Coding.Contains(toolId),
        Current => Common.Contains(toolId) || Coding.Contains(toolId) || toolId is "run_code" or "apply_patch",
        _ => throw new AgentProtocolException("tool_profile_invalid"),
    };

    public static string ForNewRun(string? configured = null)
    {
        if (configured is not null)
            return ReadPin(new JsonObject { [PinField] = configured });
        // One inventory for every model; a model-name guess is not quality evidence.
        return Current;
    }
}
