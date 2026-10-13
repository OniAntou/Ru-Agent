using System.Text.Json;

namespace RuAgent.Client;

/// <summary>Parses a JSON response stream without an intermediate HTTP buffer or UTF-16 body.</summary>
public static class BoundedJsonResponseReader
{
    public static async Task<JsonDocument> ReadAsync(HttpContent content, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > maximumBytes)
            throw TooLarge();

        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bounded = new LimitedReadStream(source, maximumBytes);
        try
        {
            return await JsonDocument.ParseAsync(bounded, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException error)
        {
            throw new AgentApiException("invalid_response", "Invalid JSON response.", inner: error);
        }
    }

    private static AgentApiException TooLarge() => new("response_too_large", "response_too_large: export response exceeds the client limit.");

    private sealed class LimitedReadStream(Stream source, int maximumBytes) : Stream
    {
        private long _bytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, Math.Min(count, RemainingProbeBytes));
            Account(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await source.ReadAsync(buffer[..Math.Min(buffer.Length, RemainingProbeBytes)], cancellationToken).ConfigureAwait(false);
            Account(read);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        // At the exact bound, read one byte to distinguish EOF from overflow.
        private int RemainingProbeBytes => (int)Math.Min(int.MaxValue, maximumBytes - _bytes + 1);
        private void Account(int read)
        {
            _bytes += read;
            if (_bytes > maximumBytes) throw TooLarge();
        }
    }
}
