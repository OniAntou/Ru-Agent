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
/// newline keeps back-to-back frames intact. An oversized line is drained to its
/// newline before reporting TooLarge so the next read starts on a frame boundary
/// instead of the middle of the rejected line.
/// </summary>
public static class RpcFrameCodec
{
    public static async Task<RpcFrameReadResult> ReadFrameAsync(
        TextReader reader,
        int maxCharacters,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(4096, maxCharacters));
        var buffer = new char[1];
        var oversized = false;
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

            if (builder.Length >= maxCharacters)
            {
                oversized = true;
                builder.Clear();
                continue;
            }

            builder.Append(c);
        }
    }

    public static async Task WriteFrameAsync(TextWriter writer, JsonNode message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }
}
