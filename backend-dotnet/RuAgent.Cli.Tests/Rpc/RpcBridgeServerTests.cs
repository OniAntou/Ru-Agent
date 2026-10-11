using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Cli.Rpc;
using RuAgent.Client;
using Xunit;

namespace RuAgent.Cli.Tests.Rpc;

public sealed class RpcBridgeServerTests
{
    private static (RpcBridgeServer Server, StringWriter Out) CreateServer(
        string input,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        int outboundCapacity = RpcProtocol.MaxOutboundMessages)
    {
        var outWriter = new StringWriter();
        var server = CreateServerOn(new StringReader(input), outWriter, handler, outboundCapacity);
        return (server, outWriter);
    }

    private static RpcBridgeServer CreateServerOn(
        TextReader input,
        TextWriter output,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        int outboundCapacity = RpcProtocol.MaxOutboundMessages)
    {
        var session = RpcTestHost.CreateSession();
        var http = RpcTestHost.CreateClient(handler);
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
        return new RpcBridgeServer(input, output, TextWriter.Null, dispatcher, runBridge, outboundCapacity);
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

    [Fact]
    public void Backpressure_stops_accepting_publishes()
    {
        var (server, _) = CreateServer(
            string.Empty,
            _ => Task.FromResult(RpcTestHost.Json("{}")),
            outboundCapacity: 1);
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "run.event",
            ["params"] = new JsonObject { ["sequence"] = 1 },
        };

        Assert.True(server.TryPublish(notification));
        Assert.False(server.TryPublish(notification));
        Assert.False(server.TryPublish(notification));
    }

    [Fact]
    public async Task Backpressure_ends_the_session_with_exit_three_and_a_shutdown_frame()
    {
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new BlockingWriter(releaseWriter.Task);
        var reader = new BlockingAfterFirstFrameReader(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1}}");
        var server = CreateServerOn(
            reader,
            writer,
            _ => Task.FromResult(RpcTestHost.Json("{}")),
            outboundCapacity: 1);
        var run = server.RunAsync(CancellationToken.None);

        // The read loop parked after dispatching the first frame; the writer is
        // still holding that response, so slot one is free.
        await reader.Entered;
        var notification = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "run.event",
            ["params"] = new JsonObject { ["sequence"] = 1 },
        };
        Assert.True(server.TryPublish(notification));
        Assert.False(server.TryPublish(notification));

        // Releasing the writer lets the queue drain and the shutdown frame out.
        releaseWriter.SetResult();
        var exit = await run;

        Assert.Equal(3, exit);
        Assert.Contains("\"reason\":\"backpressure\"", writer.ToString());
    }

    [Fact]
    public async Task Publishing_after_the_writer_completed_does_not_terminate()
    {
        var (server, _) = CreateServer(
            string.Empty,
            _ => Task.FromResult(RpcTestHost.Json("{}")));
        var exit = await server.RunAsync(CancellationToken.None);
        Assert.Equal(0, exit);
        var late = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "run.detached",
            ["params"] = new JsonObject { ["subscription_id"] = "sub_1" },
        };
        Assert.False(server.TryPublish(late));
    }

    [Fact]
    public async Task Cancellation_returns_130_and_emits_shutdown()
    {
        var (server, output) = CreateServer(string.Empty, _ => Task.FromResult(RpcTestHost.Json("{}")));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var exit = await server.RunAsync(cts.Token);
        Assert.Equal(130, exit);
        Assert.Contains("bridge.shutdown", output.ToString());
        Assert.Contains("\"reason\":\"cancelled\"", output.ToString());
    }

    /// <summary>A writer that holds every write until the release task completes.</summary>
    private sealed class BlockingWriter(Task release) : TextWriter
    {
        private readonly StringBuilder _buffer = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override async Task WriteLineAsync(string? value)
        {
            await release.ConfigureAwait(false);
            _buffer.AppendLine(value);
        }

        public override async Task FlushAsync()
        {
            await release.ConfigureAwait(false);
        }

        public override string ToString() => _buffer.ToString();
    }

    /// <summary>Serves one line, then blocks until the caller's token cancels.</summary>
    private sealed class BlockingAfterFirstFrameReader : TextReader
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly string _firstFrame;
        private int _served;

        public BlockingAfterFirstFrameReader(string firstLine) => _firstFrame = firstLine + "\n";

        public Task Entered => _entered.Task;

        public override async ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_served < _firstFrame.Length)
            {
                var count = Math.Min(buffer.Length, _firstFrame.Length - _served);
                _firstFrame.AsSpan(_served, count).CopyTo(buffer.Span);
                _served += count;
                return count;
            }

            _entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(char[] buffer, int index, int count) => throw new NotSupportedException();
    }
}
