using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RuAgent.Broker.Mcp;

public sealed record McpToolDefinition(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("inputSchema")] JsonObject InputSchema);

public sealed record McpContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null);

public sealed record McpToolCallResult(
    [property: JsonPropertyName("content")] IReadOnlyList<McpContent>? Content,
    [property: JsonPropertyName("isError")] bool IsError = false);

public sealed record JsonRpcRequest(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonNode? Params = null);

public sealed record JsonRpcNotification(
    [property: JsonPropertyName("jsonrpc")] string JsonRpc,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("params")] JsonNode? Params = null);

public sealed record JsonRpcError(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("data")] JsonNode? Data = null);
