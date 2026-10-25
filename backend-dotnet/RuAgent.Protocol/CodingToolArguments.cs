using System.Text.Json.Nodes;

namespace RuAgent.Protocol;

/// <summary>Explicit adapter reuse. The advertised identity and original arguments remain on every proposal/receipt.</summary>
public static class CodingToolArguments
{
    public static string? AdapterToolId(string toolId) => toolId switch
    {
        "read_file" => "read",
        "write_file" or "file_delete" or "file_restore" => "write",
        "edit_file" => "edit",
        "powershell" or "run_process" or "terminal_read" or "terminal_stop" => "bash",
        _ => null,
    };

    public static JsonObject ToAdapterArguments(string toolId, JsonObject arguments)
    {
        if (AdapterToolId(toolId) is null) throw new ArgumentException("unknown_coding_tool");
        // Reject attempts to override the operation encoded by this tool's identity.
        if (arguments.ContainsKey("op") || arguments.ContainsKey("mode") || arguments.ContainsKey("shell"))
            throw new ArgumentException("invalid_arguments");
        var result = (JsonObject)arguments.DeepClone();
        switch (toolId)
        {
            case "write_file": result["op"] = "write"; break;
            case "file_delete": result["op"] = "delete"; break;
            case "file_restore": result["op"] = "restore"; break;
            case "powershell": result["mode"] = "shell"; result["shell"] = "powershell"; break;
            case "run_process": result["mode"] = "run"; break;
            case "terminal_read": result["mode"] = "read_output"; break;
            case "terminal_stop": result["mode"] = "kill"; break;
        }
        return result;
    }
}
