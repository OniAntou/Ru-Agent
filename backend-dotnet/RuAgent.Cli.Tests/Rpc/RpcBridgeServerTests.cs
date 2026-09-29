using System.Text.Json;
using RuAgent.Cli.Rpc;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests.Rpc;

public sealed class RpcBridgeServerTests
{
    private static (RpcBridgeServer Server, StringWriter Out) CreateServer(
        string input,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var session = RpcTestHost.CreateSession();
        var http = RpcTestHost.CreateClient(handler);
        var outWriter = new StringWriter();
        var runBridge = new RpcRunBridge(
            new AgentSseClient(session, http),
            new AgentApiClient(session, http),
            publish: _ => true);
        var dispatcher = new RpcDispatcher(
            session,
            new SessionApiClient(session, http),
            new AgentApiClient(session, http),
            new SettingsApiClient(session, http),
            runStarter: null,
            runBridge,
            log: TextWriter.Null,
            httpClient: http);
        var server = new RpcBridgeServer(new StringReader(input), outWriter, TextWriter.Null, dispatcher, runBridge);
        return (server, outWriter);
    }

    [Fact]
    public async Task Handshake_then_status_round_trips_as_json_lines()
    {
        var input =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"host.status\",\"params\":{}}\n";
        var (server, output) = CreateServer(input, _ => Task.FromResult(RpcTestHost.Json("{\"status\":\"ok\"}")));
        var exit = await server.RunAsync(CancellationToken.None);
        Assert.Equal(0, exit);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal(1, first.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(1, first.RootElement.GetProperty("result").GetProperty("protocolVersion").GetInt32());
        using var second = JsonDocument.Parse(lines[1]);
        Assert.True(second.RootElement.GetProperty("result").GetProperty("ready").GetBoolean());
        using var last = JsonDocument.Parse(lines[2]);
        Assert.Equal("bridge.shutdown", last.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task A_broken_line_returns_a_parse_error_and_the_loop_continues()
    {
        var input =
            "not json\n" +
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}\n";
        var (server, output) = CreateServer(input, _ => Task.FromResult(RpcTestHost.Json("{}")));
        var exit = await server.RunAsync(CancellationToken.None);
        Assert.Equal(0, exit);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, lines.Length);
        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal(-32700, first.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Stdin_eof_emits_a_shutdown_notification_and_exits_zero()
    {
        var (server, output) = CreateServer(string.Empty, _ => Task.FromResult(RpcTestHost.Json("{}")));
        var exit = await server.RunAsync(CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Contains("bridge.shutdown", output.ToString());
    }

    [Fact]
    public async Task An_oversized_line_returns_request_too_large_and_continues()
    {
        var oversized = new string('x', (1024 * 1024) + 16);
        var input = oversized + "\n{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}\n";
        var (server, output) = CreateServer(input, _ => Task.FromResult(RpcTestHost.Json("{}")));
        var exit = await server.RunAsync(CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Contains("request_too_large", output.ToString());
        Assert.Contains("\"protocolVersion\":1", output.ToString());
    }
}
