using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Mcp;

namespace RuAgent.Broker.Tests.Mcp;

/// <summary>
/// An in-process MCP server over anonymous pipes. <see cref="Reply"/> answers each request by
/// method; returning null leaves it unanswered (to exercise deadlines).
/// </summary>
internal sealed class FakeMcpServer : IAsyncDisposable
{
    private readonly AnonymousPipeServerStream _toServer = new(PipeDirection.In);
    private readonly AnonymousPipeServerStream _toClient = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream _clientWrite;
    private readonly AnonymousPipeClientStream _clientRead;
    private readonly StreamWriter _writer;
    private readonly Task _loop;
    private readonly List<JsonObject> _requests = [];

    public FakeMcpServer(Func<string, JsonObject, JsonNode?> reply)
    {
        Reply = reply;
        _clientWrite = new AnonymousPipeClientStream(PipeDirection.Out, _toServer.ClientSafePipeHandle);
        _clientRead = new AnonymousPipeClientStream(PipeDirection.In, _toClient.ClientSafePipeHandle);
        _writer = new StreamWriter(_toClient, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _loop = Task.Run(LoopAsync);
    }

    public Func<string, JsonObject, JsonNode?> Reply { get; }

    public IReadOnlyList<JsonObject> Requests
    {
        get { lock (_requests) return _requests.ToArray(); }
    }

    public McpStdioClient Connect() => new(_clientRead, _clientWrite);

    /// <summary>Writes a raw line to the client, as a server would.</summary>
    public Task SendRawAsync(string line) => _writer.WriteLineAsync(line);

    private async Task LoopAsync()
    {
        using var reader = new StreamReader(_toServer, new UTF8Encoding(false));
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (JsonNode.Parse(line) is not JsonObject request) continue;
                lock (_requests) _requests.Add(request);
                if (request["id"] is null) continue;
                var method = request["method"]!.GetValue<string>();
                var result = Reply(method, request["params"] as JsonObject ?? new JsonObject());
                if (result is null) continue;
                var response = result is JsonObject { } error && error.ContainsKey("__error")
                    ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["error"] = new JsonObject { ["code"] = -32000, ["message"] = error["__error"]!.DeepClone() } }
                    : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result };
                await _writer.WriteLineAsync(response.ToJsonString());
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException)
        {
            // The test disposed the pipes; the server simply stops.
        }
    }

    public static JsonObject Text(string text) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
    };

    public static JsonNode? Standard(string method, JsonObject parameters) => method switch
    {
        "initialize" => new JsonObject { ["protocolVersion"] = McpStdioClient.ProtocolVersion, ["capabilities"] = new JsonObject() },
        "tools/list" => new JsonObject
        {
            ["tools"] = new JsonArray(new JsonObject
            {
                ["name"] = "echo",
                ["description"] = "Echo the message back.",
                ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["message"] = new JsonObject { ["type"] = "string" } } },
            }),
        },
        "tools/call" => Text("echo: " + parameters["arguments"]?["message"]?.GetValue<string>()),
        _ => new JsonObject(),
    };

    public async ValueTask DisposeAsync()
    {
        await _writer.DisposeAsync();
        await _clientWrite.DisposeAsync();
        await _clientRead.DisposeAsync();
        await _toServer.DisposeAsync();
        try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
        await _toClient.DisposeAsync();
    }
}
