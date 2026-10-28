using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Broker.Tools.Web;
using RuAgent.Broker.Tools.Help;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private static string SafeAction(ToolManifest manifest) => manifest.Id switch
    {
        "powershell" or "run_process" => "Run a terminal action in the workspace.",
        "mcp" => "Start or call one of your MCP servers.",
        "edit_file" => "Chỉnh sửa file.",
        _ => "Run an external or destructive Agent action.",
    };

    internal static string BuildPreview(string toolId, JsonObject arguments, string? fileText = null)
    {
        try
        {
            if (arguments is null)
            {
                return string.Empty;
            }

            if (toolId == "edit_file")
                return BuildEditPreview(arguments["edits"]?.AsArray(), fileText);
            if (toolId == "apply_patch") return Bound(ReadOptionalString(arguments, "patch") ?? string.Empty);
            var preview = toolId switch
            {
                "write_file" => ReadOptionalString(arguments, "content") ?? string.Empty,
                "powershell" => ReadOptionalString(arguments, "script") ?? string.Empty,
                "run_process" => TerminalRunPreview(arguments),
                "terminal_read" => "Read retained terminal output.",
                // ADR 0025: the card names the server, the tool and the bounded arguments.
                "mcp" => ReadOptionalString(arguments, "op") == "call"
                    ? $"{ReadOptionalString(arguments, "server")} > {ReadOptionalString(arguments, "tool")} {Bound(ReadOptionalString(arguments, "arguments") ?? "{}")}"
                    : $"{ReadOptionalString(arguments, "server")} > list tools",
                _ => string.Empty,
            };
            return preview.Length <= ApprovalPreviewFields.MaxLength
                ? preview
                : preview[..ApprovalPreviewFields.MaxLength];
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Bound(string value) =>
        value.Length <= ApprovalPreviewFields.MaxLength / 2 ? value : value[..(ApprovalPreviewFields.MaxLength / 2)];
}
