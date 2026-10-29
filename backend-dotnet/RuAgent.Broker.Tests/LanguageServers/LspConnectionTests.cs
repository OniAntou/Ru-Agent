using System.Text.Json.Nodes;
using System.Threading.Channels;
using RuAgent.Broker.Tools.LanguageServers;
using Xunit;

namespace RuAgent.Broker.Tests.LanguageServers;

public sealed class LspConnectionTests
{
    [Fact]
    public async Task CancellationWritesBoundedCancelNotificationAndPreservesCancellation()
    {
        var clientToServer = new ChannelStream();
        var serverToClient = new ChannelStream();
        await using var connection = new LspConnection(serverToClient, clientToServer);
        using var cancelled = new CancellationTokenSource();
        var request = connection.RequestAsync("textDocument/definition", new JsonObject(), cancelled.Token);
        var sent = JsonNode.Parse(await LspFraming.ReadAsync(clientToServer, CancellationToken.None) ?? throw new InvalidDataException());
        cancelled.Cancel();

        var cancel = JsonNode.Parse(await LspFraming.ReadAsync(clientToServer, CancellationToken.None) ?? throw new InvalidDataException());
        Assert.Equal("$/cancelRequest", cancel!["method"]!.GetValue<string>());
        Assert.Equal(sent!["id"]!.GetValue<long>(), cancel["params"]!["id"]!.GetValue<long>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task UnsupportedServerRequestsReceiveJsonRpcMethodNotFound()
    {
        var clientToServer = new ChannelStream();
        var serverToClient = new ChannelStream();
        await using var connection = new LspConnection(serverToClient, clientToServer);
        var request = connection.RequestAsync("initialize", new JsonObject(), CancellationToken.None);
        var clientRequest = JsonNode.Parse(await LspFraming.ReadAsync(clientToServer, CancellationToken.None) ?? throw new InvalidDataException());
        await LspFraming.WriteAsync(serverToClient,
            System.Text.Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":"server-1","method":"window/showMessageRequest","params":{}}"""), CancellationToken.None);
        var response = JsonNode.Parse(await LspFraming.ReadAsync(clientToServer, CancellationToken.None) ?? throw new InvalidDataException());

        Assert.Equal("server-1", response!["id"]!.GetValue<string>());
        Assert.Equal(-32601, response["error"]!["code"]!.GetValue<int>());
        await LspFraming.WriteAsync(serverToClient,
            System.Text.Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{clientRequest!["id"]!.GetValue<long>()},\"result\":{{}}}}"), CancellationToken.None);
        Assert.NotNull(await request);
    }

    [Fact]
    public async Task CallerCancellationInterruptsABlockedFrameWrite()
    {
        await using var connection = new LspConnection(new ChannelStream(), new BlockingWriteStream());
        using var cancelled = new CancellationTokenSource();
        var request = connection.RequestAsync("initialize", new JsonObject(), cancelled.Token);
        await Task.Delay(20);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task MatchesOutOfOrderResponsesByRequestId()
    {
        var clientToServer = new ChannelStream();
        var serverToClient = new ChannelStream();
        await using var connection = new LspConnection(serverToClient, clientToServer);
        var first = connection.RequestAsync("first", new JsonObject(), CancellationToken.None);
        var second = connection.RequestAsync("second", new JsonObject(), CancellationToken.None);
        var one = JsonNode.Parse(await LspFraming.ReadAsync(clientToServer, CancellationToken.None) ?? throw new InvalidDataException());
        var two = JsonNode.Parse(await LspFraming.ReadAsync(clientToServer, CancellationToken.None) ?? throw new InvalidDataException());
        await SendResultAsync(serverToClient, two!["id"]!.GetValue<long>(), "second-result");
        await SendResultAsync(serverToClient, one!["id"]!.GetValue<long>(), "first-result");

        Assert.Equal("first-result", (await first)!.GetValue<string>());
        Assert.Equal("second-result", (await second)!.GetValue<string>());
    }

    [Fact]
    public async Task ServerExitCompletesPendingRequestWithNamedFailure()
    {
        await using var connection = new LspConnection(new MemoryStream(), new MemoryStream());
        var error = await Assert.ThrowsAsync<LspException>(() => connection.RequestAsync("initialize", new JsonObject(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("lsp_server_unavailable", error.Code);
    }

    [Fact]
    public async Task RequestAfterServerExitFailsImmediately()
    {
        await using var connection = new LspConnection(new MemoryStream(), new MemoryStream());
        await connection.ReaderCompletion.WaitAsync(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<LspException>(() => connection.RequestAsync("initialize", new JsonObject(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("lsp_server_unavailable", error.Code);
    }

    private static Task SendResultAsync(Stream stream, long id, string value) =>
        LspFraming.WriteAsync(stream, System.Text.Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"result\":\"{value}\"}}"), CancellationToken.None);

    private sealed class ChannelStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        private byte[]? _current;
        private int _offset;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return ReadCoreAsync(buffer, cancellationToken);
        }
        private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            while (_current is null || _offset == _current.Length)
            {
                _current = await _channel.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
        public override void Write(byte[] buffer, int offset, int count) => _channel.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray());
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _channel.Writer.TryWrite(buffer.ToArray());
            return ValueTask.CompletedTask;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class BlockingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(Task.Delay(Timeout.Infinite, cancellationToken));
    }
}
