using System.Text.Json;
using RuAgent.SessionInterchange;

namespace RuBackend.Business;

/// <summary>Measures the actual export JSON without allocating another copy of the envelope.</summary>
public static class SessionExportEnvelopeLimit
{
    public static async Task<bool> FitsAsync<T>(T envelope, CancellationToken cancellationToken,
        int maximumBytes = SessionInterchangeLimits.MaxExportBodyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        cancellationToken.ThrowIfCancellationRequested();
        using var counter = new SizeCounter(maximumBytes);
        try
        {
            await JsonSerializer.SerializeAsync(counter, envelope, JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (LimitExceededException)
        {
            return false;
        }
    }

    private sealed class LimitExceededException : Exception;

    private sealed class SizeCounter(int maximumBytes) : Stream
    {
        private long _bytes;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytes;
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Account(count);
        public override void Write(ReadOnlySpan<byte> buffer) => Account(buffer.Length);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Account(buffer.Length);
            return ValueTask.CompletedTask;
        }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Account(int count)
        {
            _bytes += count;
            if (_bytes > maximumBytes) throw new LimitExceededException();
        }
    }
}
