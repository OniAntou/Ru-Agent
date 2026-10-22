using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Configuration;
using RuAgent.Broker.Runtime;
using RuAgent.Broker.Tools.Desktop;
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
        "bash" => "Run a terminal action in the workspace.",
        "mcp" => "Start or call one of your MCP servers.",
        "edit" => "Chỉnh sửa file.",
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

            var preview = toolId switch
            {
                "write" => ReadOptionalString(arguments, "content") ?? string.Empty,
                "edit" => EditPreview(arguments, fileText),
                "bash" => IsShellArgs(arguments)
                    ? ReadOptionalString(arguments, "script") ?? string.Empty
                    : TerminalRunPreview(arguments),
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

    private static bool IsShellArgs(JsonObject arguments) =>
        string.Equals(arguments["mode"]?.GetValue<string>(), "shell", StringComparison.Ordinal);

    private static string Bound(string value) =>
        value.Length <= ApprovalPreviewFields.MaxLength / 2 ? value : value[..(ApprovalPreviewFields.MaxLength / 2)];
}
