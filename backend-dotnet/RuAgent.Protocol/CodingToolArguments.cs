using System.Text.Json.Nodes;
using RuAgent.Protocol.Tools;

namespace RuAgent.Protocol;

/// <summary>Internal operation-family normalization for policy and receipt projections, never execution dispatch. Proposals and receipts retain their advertised identity and original arguments.</summary>
public static class CodingToolArguments
{
    /// <summary>The policy family of an advertised tool id, read from its descriptor row; null when the id has no row or the row has no family.</summary>
    public static string? AdapterToolId(string toolId) => AgentToolDescriptorTable.Find(toolId)?.PolicyFamily;

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
