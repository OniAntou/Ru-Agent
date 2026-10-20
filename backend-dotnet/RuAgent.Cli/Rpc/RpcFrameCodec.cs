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
/// Reads and writes one NDJSON frame at a time. The frame cap is measured in
/// UTF-8 bytes, matching the on-the-wire limit, so multi-byte text cannot creep
/// past it. An oversized line is drained to its newline before reporting
/// TooLarge so the next read starts on a frame boundary instead of the middle of
/// the rejected line.
/// </summary>
public static class RpcFrameCodec
{
    // The buffered reader state must survive between frames on the same stream:
    // a block read can return the start of the next frame, so a fresh reader per
    // call would lose those characters. Callers that read a stream more than once
    // therefore share one reader per TextReader instance.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextReader, RpcFrameReader> Readers = new();

    public static Task<RpcFrameReadResult> ReadFrameAsync(
        TextReader reader,
        int maxFrameBytes,
        CancellationToken cancellationToken) =>
        Readers.GetValue(reader, source => new RpcFrameReader(source, maxFrameBytes))
            .ReadFrameAsync(cancellationToken);

    /// <summary>
    /// Counts the UTF-8 bytes of one code unit. A high surrogate costs 3 bytes until
    /// its low surrogate follows, at which point the pair costs 4 in total; an
    /// unpaired high surrogate costs the 3 bytes of the replacement character it
    /// encodes to.
    /// </summary>
    internal static int Utf8ByteIncrement(char c, ref char? pendingHighSurrogate)
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

/// <summary>
/// One frame at a time from a text stream, buffering whole blocks between
/// frames. Reading character-by-character made a megabyte frame a million
/// awaits; the block buffer keeps the framing rule intact -- the characters
/// after a frame's newline stay buffered for the next frame instead of being
/// lost, which a one-shot block read could not guarantee.
/// </summary>
public sealed class RpcFrameReader
{
    private const int BufferChars = 4096;
    private readonly TextReader _reader;
    private readonly int _maxFrameBytes;
    private readonly char[] _buffer = new char[BufferChars];
    private int _start;
    private int _count;
    private bool _atStreamStart = true;

    public RpcFrameReader(TextReader reader, int maxFrameBytes)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _maxFrameBytes = maxFrameBytes;
    }

    public async Task<RpcFrameReadResult> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(4096, _maxFrameBytes));
        var oversized = false;
        var bytes = 0;
        char? pendingHighSurrogate = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_start == _count)
            {
                _count = await _reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _start = 0;
                if (_count == 0)
                {
                    if (oversized)
                    {
                        return new RpcFrameReadResult(RpcFrameStatus.TooLarge, string.Empty);
                    }

                    return builder.Length == 0
                        ? RpcFrameReadResult.EndOfInput
                        : new RpcFrameReadResult(RpcFrameStatus.Line, builder.ToString());
                }
            }

            var c = _buffer[_start++];
            if (_atStreamStart)
            {
                // A Windows writer (Windows PowerShell's redirected stdin among them) can
                // emit a UTF-8 preamble before the first frame. A byte order mark is not
                // JSON content, so drop exactly one at the very start of the stream.
                _atStreamStart = false;
                if (c == '﻿')
                {
                    continue;
                }
            }

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

            var byteIncrement = RpcFrameCodec.Utf8ByteIncrement(c, ref pendingHighSurrogate);
            if (bytes + byteIncrement > _maxFrameBytes)
            {
                oversized = true;
                builder.Clear();
                continue;
            }

            bytes += byteIncrement;
            builder.Append(c);
        }
    }
}
