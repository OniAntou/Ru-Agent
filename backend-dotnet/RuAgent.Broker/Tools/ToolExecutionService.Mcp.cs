using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Mcp;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private McpServerHost? _mcpServers;

    /// <summary>Connects the Host's MCP servers; without it every <c>mcp</c> call fails by name.</summary>
    public void UseMcpServers(McpServerHost servers) => _mcpServers = servers ?? throw new ArgumentNullException(nameof(servers));

    private async ValueTask<ToolExecutionOutcome> ExecuteMcpAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        _ = context;
        if (_mcpServers is not { } servers)
        {
            return ToolExecutionOutcome.Failed("mcp_unavailable", "MCP servers are not available in this host.");
        }

        var op = ReadOptionalString(arguments, "op");
        try
        {
            return op switch
            {
                "list_servers" => ToolExecutionOutcome.Succeeded(
                    servers.ListServers(), ToolVerificationStates.NotRequired, "MCP servers listed."),
                "list_tools" => ToolExecutionOutcome.Succeeded(
                    await servers.ListToolsAsync(Required(arguments, "server"), cancellationToken).ConfigureAwait(false),
                    ToolVerificationStates.NotRequired, "MCP tools listed."),
                "call" => ToolExecutionOutcome.Succeeded(
                    await servers.CallToolAsync(
                        Required(arguments, "server"),
                        Required(arguments, "tool"),
                        ReadOptionalString(arguments, "arguments") ?? "{}",
                        cancellationToken).ConfigureAwait(false),
                    ToolVerificationStates.NotRequired, "MCP tool completed."),
                _ => ToolExecutionOutcome.Failed("mcp_arguments_invalid", "op must be list_servers, list_tools or call."),
            };
        }
        catch (McpException error)
        {
            return ToolExecutionOutcome.Failed(error.Code, error.Detail ?? error.Code);
        }
    }

    private static string Required(JsonObject arguments, string name) =>
        ReadOptionalString(arguments, name) is { Length: > 0 } value
            ? value
            : throw new McpException("mcp_arguments_invalid", $"{name} is required.");
}
