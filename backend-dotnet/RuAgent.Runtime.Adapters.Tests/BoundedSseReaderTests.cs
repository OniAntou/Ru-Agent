using System.Text;
using RuAgent.Runtime.Adapters.Models;
using Xunit;

namespace RuAgent.Runtime.Adapters.Tests;

public sealed class BoundedSseReaderTests
{
    private static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));
    private static MemoryStream Raw(byte[] bytes) => new(bytes);

    private static async Task<List<ServerSentEvent>> DrainAsync(Stream s, CancellationToken ct = default)
    {
        var list = new List<ServerSentEvent>();
        await foreach (var e in BoundedSseReader.ReadAsync(s, ct))
            list.Add(e);
        return list;
    }

    [Fact]
    public async Task Reads_LF_framed_event()
    {
        var s = Stream("event: foo\ndata: bar\n\n");
        var events = await DrainAsync(s);
        Assert.Single(events);
        Assert.Equal("foo", events[0].Event);
        Assert.Equal("bar", events[0].Data);
    }

    [Fact]
    public async Task Reads_CRLF_framed_event()
    {
        var s = Stream("event: foo\r\ndata: bar\r\n\r\n");
        var events = await DrainAsync(s);
        Assert.Single(events);
        Assert.Equal("foo", events[0].Event);
        Assert.Equal("bar", events[0].Data);
    }

    [Fact]
    public async Task Reads_event_with_multi_data_joined_by_newline()
    {
        var s = Stream("event: msg\ndata: a\ndata: b\n\n");
        var events = await DrainAsync(s);
        Assert.Single(events);
        Assert.Equal("msg", events[0].Event);
        Assert.Equal("a\nb", events[0].Data);
    }

    [Fact]
    public async Task Ignores_comment_lines()
    {
        var s = Stream(": comment line\n: another\n" + "data: hi\n\n");
        var events = await DrainAsync(s);
        Assert.Single(events);
        Assert.Equal("hi", events[0].Data);
        Assert.Null(events[0].Event);
    }

    [Fact]
    public async Task Strips_BOM_at_start()
    {
        var bom = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes("data: hi\n\n");
        var bytes = new byte[bom.Length + body.Length];
        Buffer.BlockCopy(bom, 0, bytes, 0, bom.Length);
        Buffer.BlockCopy(body, 0, bytes, bom.Length, body.Length);
        var events = await DrainAsync(Raw(bytes));
        Assert.Single(events);
        Assert.Equal("hi", events[0].Data);
    }

    [Fact]
    public async Task Rejects_strict_UTF8_invalid_bytes_as_malformed_payload()
    {
        var bytes = new byte[] { 0xFF, 0xFF, (byte)'\n', (byte)'\n' };
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await DrainAsync(Raw(bytes)));
        Assert.Equal("malformed_payload", ex.Code);
        Assert.False(ex.Retryable);
    }

    [Fact]
    public async Task Rejects_line_over_256KiB()
    {
        var payload = new string('a', 256 * 1024);
        var s = Stream($"data: {payload}\n\n");
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await DrainAsync(s));
        Assert.Equal("response_too_large", ex.Code);
    }

    [Fact]
    public async Task Rejects_oversized_unterminated_line_before_consuming_the_whole_stream()
    {
        var bytes = Enumerable.Repeat((byte)'a', 1024 * 1024).ToArray();
        var stream = Raw(bytes);

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await DrainAsync(stream));

        Assert.Equal("response_too_large", ex.Code);
        Assert.InRange(stream.Position, 1, 256 * 1024 + 4096);
    }

    [Fact]
    public async Task Reads_CR_only_framed_event_without_inserting_a_blank_line()
    {
        var events = await DrainAsync(Stream("event: foo\rdata: bar\r\r"));

        var parsed = Assert.Single(events);
        Assert.Equal("foo", parsed.Event);
        Assert.Equal("bar", parsed.Data);
    }

    [Fact]
    public async Task Rejects_event_data_over_256KiB()
    {
        var chunk = new string('a', 100 * 1024);
        var sb = new StringBuilder();
        sb.Append($"data: {chunk}\n");
        sb.Append($"data: {chunk}\n");
        sb.Append($"data: {chunk}\n");
        sb.Append("\n");
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await DrainAsync(Stream(sb.ToString())));
        Assert.Equal("response_too_large", ex.Code);
    }

    [Fact]
    public async Task Rejects_total_over_5MiB()
    {
        var chunk = new string('x', 200 * 1024);
        var sb = new StringBuilder();
        for (int i = 0; i < 26; i++)
            sb.Append($"data: {chunk}\n\n");
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await DrainAsync(Stream(sb.ToString())));
        Assert.Equal("response_too_large", ex.Code);
    }

    [Fact]
    public async Task Rejects_EOF_with_unfinished_event_as_malformed_payload()
    {
        var s = Stream("data: hello\n");
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await DrainAsync(s));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task Rejects_EOF_with_pending_event_name_but_no_blank_line()
    {
        var s = Stream("event: foo\ndata: bar\n");
        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () => await DrainAsync(s));
        Assert.Equal("malformed_payload", ex.Code);
    }

    [Fact]
    public async Task Cancellation_is_observed()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var s = Stream("data: hi\n\n");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await DrainAsync(s, cts.Token));
    }

    [Fact]
    public async Task Empty_stream_yields_no_events()
    {
        var events = await DrainAsync(Stream(string.Empty));
        Assert.Empty(events);
    }

    [Fact]
    public async Task Multiple_events_are_all_returned()
    {
        var s = Stream("data: one\n\n" + "data: two\n\n");
        var events = await DrainAsync(s);
        Assert.Equal(2, events.Count);
        Assert.Equal("one", events[0].Data);
        Assert.Equal("two", events[1].Data);
    }

    [Fact]
    public async Task Idle_provider_times_out_as_retryable_timeout()
    {
        var stream = new StallingStream(prefix: string.Empty);

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var _ in BoundedSseReader.ReadAsync(
                stream,
                CancellationToken.None,
                TimeSpan.FromMilliseconds(100)))
            {
            }
        });

        Assert.Equal("timeout", ex.Code);
        Assert.True(ex.Retryable);
    }

    [Fact]
    public async Task Idle_timeout_resets_after_each_received_chunk()
    {
        var stream = new StallingStream(prefix: "data: one\n\n");
        var events = new List<ServerSentEvent>();

        var ex = await Assert.ThrowsAsync<ModelProviderException>(async () =>
        {
            await foreach (var e in BoundedSseReader.ReadAsync(
                stream,
                CancellationToken.None,
                TimeSpan.FromMilliseconds(300)))
            {
                events.Add(e);
            }
        });

        Assert.Equal("timeout", ex.Code);
        Assert.Single(events);
        Assert.Equal("one", events[0].Data);
    }

    /// <summary>A stream that serves a prefix once, then never produces another byte.</summary>
    private sealed class StallingStream(string prefix) : Stream
    {
        private readonly byte[] _prefix = Encoding.UTF8.GetBytes(prefix);
        private int _served;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_served < _prefix.Length)
            {
                var count = Math.Min(buffer.Length, _prefix.Length - _served);
                _prefix.AsSpan(_served, count).CopyTo(buffer.Span);
                _served += count;
                return count;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
