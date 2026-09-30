using System.Text;
using System.Text.Json.Nodes;

namespace RuAgent.Cli.Rpc;

public enum RpcFrameStatus
{
    EndOfInput,
    Line,
    TooLarge,
}

public sealed record RpcFrameReadResult(RpcFrameStatus Status, string Line)
{
    public static readonly RpcFrameReadResult EndOfInput = new(RpcFrameStatus.EndOfInput, string.Empty);
}

/// <summary>
/// Reads and writes one NDJSON frame at a time. Characters are consumed one at a
/// time because a TextReader is free to return the beginning of the next frame in
/// the same read, and there is no way to push it back; reading exactly through the
/// newline keeps back-to-back frames intact. The cap is measured in UTF-8 bytes,
/// matching the on-the-wire limit, so multi-byte text cannot creep past it. An
/// oversized line is drained to its newline before reporting TooLarge so the next
/// read starts on a frame boundary instead of the middle of the rejected line.
/// </summary>
public static class RpcFrameCodec
{
    public static async Task<RpcFrameReadResult> ReadFrameAsync(
        TextReader reader,
        int maxFrameBytes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(4096, maxFrameBytes));
        var buffer = new char[1];
        var oversized = false;
        var bytes = 0;
        char? pendingHighSurrogate = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (oversized)
                {
                    return new RpcFrameReadResult(RpcFrameStatus.TooLarge, string.Empty);
                }

                return builder.Length == 0
                    ? RpcFrameReadResult.EndOfInput
                    : new RpcFrameReadResult(RpcFrameStatus.Line, builder.ToString());
            }

            var c = buffer[0];
            if (c == '\n')
            {
                return oversized
                    ? new RpcFrameReadResult(RpcFrameStatus.TooLarge, string.Empty)
                    : new RpcFrameReadResult(RpcFrameStatus.Line, builder.ToString());
            }

            if (c == '\r' || oversized)
            {
                continue;
            }

            var byteIncrement = Utf8ByteIncrement(c, ref pendingHighSurrogate);
            if (bytes + byteIncrement > maxFrameBytes)
            {
                oversized = true;
                builder.Clear();
                continue;
            }

            bytes += byteIncrement;
            builder.Append(c);
        }
    }

    /// <summary>
    /// Counts the UTF-8 bytes of one code unit. A high surrogate costs 3 bytes until
    /// its low surrogate follows, at which point the pair costs 4 in total; an
    /// unpaired high surrogate costs the 3 bytes of the replacement character it
    /// encodes to.
    /// </summary>
    private static int Utf8ByteIncrement(char c, ref char? pendingHighSurrogate)
    {
        if (pendingHighSurrogate is not null)
        {
            pendingHighSurrogate = null;
            if (char.IsLowSurrogate(c))
            {
                return 1;
            }

            if (char.IsHighSurrogate(c))
            {
                pendingHighSurrogate = c;
                return 3;
            }

            return Utf8ByteCount(c);
        }

        if (char.IsHighSurrogate(c))
        {
            pendingHighSurrogate = c;
            return 3;
        }

        return Utf8ByteCount(c);
    }

    private static int Utf8ByteCount(char c) => c switch
    {
        < '\u0080' => 1,
        < '\u0800' => 2,
        _ => 3,
    };

    public static async Task WriteFrameAsync(TextWriter writer, JsonNode message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }
}
