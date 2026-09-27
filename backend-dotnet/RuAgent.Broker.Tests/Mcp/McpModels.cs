using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace RuAgent.Broker.Tests.Mcp;

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
