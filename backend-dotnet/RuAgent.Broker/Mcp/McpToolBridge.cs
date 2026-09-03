using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;

namespace RuAgent.Broker.Mcp;

public static class McpToolBridge
{
    public static async Task<IReadOnlyList<ToolBinding>> DiscoverToolsAsync(
        string serverName,
        McpClient client,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverName);
        ArgumentNullException.ThrowIfNull(client);

        var mcpTools = await client.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        var bindings = new List<ToolBinding>(mcpTools.Count);

        foreach (var tool in mcpTools)
        {
            var normalizedName = NormalizeToolName(tool.Name);
            var toolId = $"mcp.{serverName.ToLowerInvariant()}.{normalizedName}";

            var manifest = new ToolManifest(
                Id: toolId,
                Version: "1.0.0",
                Adapter: "connector",
                Risk: RiskClass.Reversible,
                Scopes: new HashSet<string>([ToolScope.WorkspaceRead], StringComparer.Ordinal),
                InputSchema: tool.InputSchema,
                OutputSchema: new JsonObject { ["type"] = "object" },
                TimeoutMs: 60_000,
                MaxOutputBytes: 256 * 1024,
                TrustClass: "allowlisted",
                RequiresApproval: false);

            bindings.Add(new ToolBinding(
                manifest,
                async (context, args, ct) =>
                {
                    var result = await client.CallToolAsync(tool.Name, args, ct).ConfigureAwait(false);
                    if (result.IsError)
                    {
                        var errorSummary = result.Content is not null && result.Content.Count > 0
                            ? string.Join("\n", result.Content.Select(c => c.Text))
                            : "mcp_tool_execution_failed";

                        return ToolExecutionOutcome.Failed("mcp_error", errorSummary);
                    }

                    var outputJson = new JsonObject();
                    if (result.Content is not null)
                    {
                        var textContent = string.Join("\n", result.Content.Select(c => c.Text));
                        outputJson["output"] = textContent;
                    }

                    return new ToolExecutionOutcome(
                        ToolResultStatuses.Succeeded,
                        outputJson,
                        null,
                        "Tool executed successfully via MCP",
                        ToolVerificationStates.NotRequired);
                },
                ModelEnabled: true,
                Availability: "available",
                VerificationRequired: false));
        }

        return bindings;
    }

    private static string NormalizeToolName(string raw)
    {
        var clean = raw.ToLowerInvariant().Replace('-', '_');
        return string.Concat(clean.Where(c => char.IsLetterOrDigit(c) || c == '_'));
    }
}
