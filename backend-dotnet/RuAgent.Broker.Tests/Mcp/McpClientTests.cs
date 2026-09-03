using System.IO.Pipelines;
using System.Text.Json.Nodes;
using RuAgent.Broker.Mcp;
using RuAgent.Broker.Policy;
using RuAgent.Broker.Tools;
using RuAgent.Protocol;
using Xunit;

namespace RuAgent.Broker.Tests.Mcp;

public sealed class McpClientTests
{
    private sealed class DuplexPipePair : IAsyncDisposable
    {
        private readonly System.IO.Pipes.AnonymousPipeServerStream _clientToServerServer;
        private readonly System.IO.Pipes.AnonymousPipeClientStream _clientToServerClient;
        private readonly System.IO.Pipes.AnonymousPipeServerStream _serverToClientServer;
        private readonly System.IO.Pipes.AnonymousPipeClientStream _serverToClientClient;

        public Stream ClientReadStream => _serverToClientClient;
        public Stream ClientWriteStream => _clientToServerClient;
        public StreamReader ServerReader { get; }
        public StreamWriter ServerWriter { get; }

        public DuplexPipePair()
        {
            _clientToServerServer = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.In);
            _clientToServerClient = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.Out, _clientToServerServer.ClientSafePipeHandle);

            _serverToClientServer = new System.IO.Pipes.AnonymousPipeServerStream(System.IO.Pipes.PipeDirection.Out);
            _serverToClientClient = new System.IO.Pipes.AnonymousPipeClientStream(System.IO.Pipes.PipeDirection.In, _serverToClientServer.ClientSafePipeHandle);

            ServerReader = new StreamReader(_clientToServerServer, System.Text.Encoding.UTF8);
            ServerWriter = new StreamWriter(_serverToClientServer, System.Text.Encoding.UTF8) { AutoFlush = true };
        }

        public async ValueTask DisposeAsync()
        {
            await ServerWriter.DisposeAsync();
            ServerReader.Dispose();
            await _clientToServerClient.DisposeAsync();
            await _clientToServerServer.DisposeAsync();
            await _serverToClientClient.DisposeAsync();
            await _serverToClientServer.DisposeAsync();
        }
    }

    [Fact]
    public async Task McpClient_InitializeAndListTools_Succeeds()
    {
        await using var pipes = new DuplexPipePair();
        await using var client = new McpClient(pipes.ClientReadStream, pipes.ClientWriteStream);

        var serverTask = Task.Run(async () =>
        {
            // 1. Expect initialize request
            var line1 = await pipes.ServerReader.ReadLineAsync();
            Assert.NotNull(line1);
            var req1 = JsonNode.Parse(line1)!;
            var id1 = req1["id"]!.GetValue<long>();
            var method1 = req1["method"]!.GetValue<string>();
            Assert.Equal("initialize", method1);

            // Respond to initialize
            var resp1 = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id1,
                ["result"] = new JsonObject
                {
                    ["protocolVersion"] = "2024-11-05",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "test-server", ["version"] = "1.0.0" }
                }
            };
            await pipes.ServerWriter.WriteLineAsync(resp1.ToJsonString());

            // 2. Expect initialized notification
            var notif = await pipes.ServerReader.ReadLineAsync();
            Assert.NotNull(notif);
            var reqNotif = JsonNode.Parse(notif)!;
            Assert.Equal("notifications/initialized", reqNotif["method"]!.GetValue<string>());

            // 3. Expect tools/list request
            var line3 = await pipes.ServerReader.ReadLineAsync();
            Assert.NotNull(line3);
            var req3 = JsonNode.Parse(line3)!;
            var id3 = req3["id"]!.GetValue<long>();
            Assert.Equal("tools/list", req3["method"]!.GetValue<string>());

            var resp3 = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id3,
                ["result"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "query_db",
                            ["description"] = "Run a database query",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["sql"] = new JsonObject { ["type"] = "string" }
                                }
                            }
                        }
                    }
                }
            };
            await pipes.ServerWriter.WriteLineAsync(resp3.ToJsonString());
        });

        await client.InitializeAsync(CancellationToken.None);
        var tools = await client.ListToolsAsync(CancellationToken.None);

        await serverTask;

        Assert.Single(tools);
        Assert.Equal("query_db", tools[0].Name);
        Assert.Equal("Run a database query", tools[0].Description);
    }

    [Fact]
    public async Task McpToolBridge_DiscoversAndExecutesTool_Successfully()
    {
        await using var pipes = new DuplexPipePair();
        await using var client = new McpClient(pipes.ClientReadStream, pipes.ClientWriteStream);

        var serverTask = Task.Run(async () =>
        {
            // Expect tools/list
            var line = await pipes.ServerReader.ReadLineAsync();
            Assert.NotNull(line);
            var req = JsonNode.Parse(line)!;
            var id = req["id"]!.GetValue<long>();

            var resp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "echo_tool",
                            ["description"] = "Echoes input",
                            ["inputSchema"] = new JsonObject
                            {
                                ["type"] = "object",
                                ["properties"] = new JsonObject
                                {
                                    ["text"] = new JsonObject { ["type"] = "string" }
                                }
                            }
                        }
                    }
                }
            };
            await pipes.ServerWriter.WriteLineAsync(resp.ToJsonString());

            // Expect tools/call
            var callLine = await pipes.ServerReader.ReadLineAsync();
            Assert.NotNull(callLine);
            var callReq = JsonNode.Parse(callLine)!;
            var callId = callReq["id"]!.GetValue<long>();
            Assert.Equal("tools/call", callReq["method"]!.GetValue<string>());

            var callResp = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = callId,
                ["result"] = new JsonObject
                {
                    ["isError"] = false,
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Echo result: Hello from MCP!"
                        }
                    }
                }
            };
            await pipes.ServerWriter.WriteLineAsync(callResp.ToJsonString());
        });

        var bindings = await McpToolBridge.DiscoverToolsAsync("demo", client, CancellationToken.None);
        Assert.Single(bindings);

        var binding = bindings[0];
        Assert.Equal("mcp.demo.echo_tool", binding.Manifest.Id);
        Assert.Equal("1.0.0", binding.Manifest.Version);

        // Verify that ToolRegistry accepts this manifest
        var registry = new ToolRegistry();
        registry.Register(binding.Manifest);
        Assert.NotNull(registry.Get(binding.Manifest.Id, binding.Manifest.Version));

        // Execute the binding
        var context = new ToolExecutionContext(
            RequestId: "req-1",
            SessionId: "sess-1",
            CorrelationId: "corr-1",
            CapabilityClass: "internal",
            DeadlineMs: 10_000,
            Approved: true,
            ApprovalToken: null,
            ConfinementRequired: true,
            SessionScopes: new HashSet<string>([ToolScope.WorkspaceRead]),
            RunId: "run-1");

        var outcome = await binding.Executor(context, new JsonObject { ["text"] = "Hello" }, CancellationToken.None);

        await serverTask;

        Assert.Equal(ToolResultStatuses.Succeeded, outcome.Status);
        Assert.NotNull(outcome.Output);
        Assert.Contains("Echo result: Hello from MCP!", outcome.Output["output"]?.ToString());
    }

    [Fact]
    public async Task McpToolBridge_HandlesToolCallError_Gracefully()
    {
        await using var pipes = new DuplexPipePair();
        await using var client = new McpClient(pipes.ClientReadStream, pipes.ClientWriteStream);

        var serverTask = Task.Run(async () =>
        {
            // tools/list
            var line = await pipes.ServerReader.ReadLineAsync();
            var id = JsonNode.Parse(line!)!["id"]!.GetValue<long>();

            await pipes.ServerWriter.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = new JsonObject
                {
                    ["tools"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = "fail_tool",
                            ["inputSchema"] = new JsonObject { ["type"] = "object" }
                        }
                    }
                }
            }.ToJsonString());

            // tools/call with error
            var callLine = await pipes.ServerReader.ReadLineAsync();
            var callId = JsonNode.Parse(callLine!)!["id"]!.GetValue<long>();

            await pipes.ServerWriter.WriteLineAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = callId,
                ["result"] = new JsonObject
                {
                    ["isError"] = true,
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "Syntax error in SQL query"
                        }
                    }
                }
            }.ToJsonString());
        });

        var bindings = await McpToolBridge.DiscoverToolsAsync("db", client, CancellationToken.None);
        var binding = bindings[0];

        var context = new ToolExecutionContext(
            RequestId: "req-2",
            SessionId: "sess-2",
            CorrelationId: "corr-2",
            CapabilityClass: "internal",
            DeadlineMs: 10_000,
            Approved: true,
            ApprovalToken: null,
            ConfinementRequired: true,
            SessionScopes: new HashSet<string>([ToolScope.WorkspaceRead]),
            RunId: "run-2");

        var outcome = await binding.Executor(context, new JsonObject(), CancellationToken.None);
        await serverTask;

        Assert.Equal(ToolResultStatuses.Failed, outcome.Status);
        Assert.Equal("mcp_error", outcome.ErrorCode);
        Assert.Contains("Syntax error in SQL query", outcome.SafeSummary);
    }

    [Fact]
    public void McpClient_StartProcess_ValidatesArgumentsAndDirectory()
    {
        Assert.Throws<ArgumentException>(() => McpClient.StartProcess("", []));
        Assert.Throws<ArgumentException>(() => McpClient.StartProcess("   ", []));
        Assert.Throws<DirectoryNotFoundException>(() => McpClient.StartProcess("cmd.exe", [], "Z:\\NonExistentPath_xyz123"));
    }
}
