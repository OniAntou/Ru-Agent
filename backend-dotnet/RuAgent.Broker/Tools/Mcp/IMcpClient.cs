using System.Text.Json.Nodes;

namespace RuAgent.Broker.Tools.Mcp;

/// <summary>The bounded request surface shared by user-configured MCP transports.</summary>
public interface IMcpClient : IAsyncDisposable
{
    bool IsAlive { get; }

    string StderrTail { get; }

    Task InitializeAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task<IReadOnlyList<McpToolInfo>> ListToolsAsync(TimeSpan timeout, CancellationToken cancellationToken);

    Task<JsonObject> CallToolAsync(
        string tool,
        System.Text.Json.Nodes.JsonObject arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
