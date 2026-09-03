using System.Text;

namespace RuBackend.Diagnostics;

internal readonly record struct BoundedTextLineResult(bool EndOfFile, bool TooLarge, string? Line, int Bytes);

/// <summary>Reads text lines without allowing a malformed line to grow without bound.</summary>
internal sealed class BoundedTextLineReader(TextReader reader, int maximumBytes) : IDisposable
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, false);
    private readonly char[] _buffer = new char[4096];
    private readonly StringBuilder _line = new(Math.Min(maximumBytes, 4096));
    private bool _eof;
    private bool _hasContent;
    private bool _tooLarge;
    private bool _skipLf;
    private int _bufferIndex;
    private int _bufferCount;
    private int _lineBytes;
    private long _consumedBytes;

    public async ValueTask<BoundedTextLineResult> ReadLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            if (_bufferIndex >= _bufferCount)
            {
                if (_eof)
                {
                    if (!_hasContent) return new(true, false, null, 0);
                    return FinishLine();
                }

                _bufferCount = await reader.ReadAsync(_buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                _bufferIndex = 0;
                if (_bufferCount == 0)
                {
                    _eof = true;
                    continue;
                }
            }

            var value = _buffer[_bufferIndex++];
            if (_skipLf)
            {
                _skipLf = false;
                if (value == '\n') continue;
            }

            if (value is '\r' or '\n')
            {
                _skipLf = value == '\r';
                return FinishLine();
            }

            _hasContent = true;
            var bytes = Utf8.GetByteCount(_buffer.AsSpan(_bufferIndex - 1, 1));
            _consumedBytes = Math.Min(int.MaxValue, _consumedBytes + bytes);
            if (_tooLarge) continue;

            if (_lineBytes > maximumBytes - bytes)
            {
                _tooLarge = true;
                _line.Clear();
                continue;
            }

            _lineBytes += bytes;
            _line.Append(value);
        }
    }

    private BoundedTextLineResult FinishLine()
    {
        var result = new BoundedTextLineResult(false, _tooLarge, _tooLarge ? null : _line.ToString(),
            (int)Math.Min(int.MaxValue, _consumedBytes + 1));
        _line.Clear();
        _lineBytes = 0;
        _hasContent = false;
        _tooLarge = false;
        _consumedBytes = 0;
        return result;
    }

    public void Dispose() => reader.Dispose();
}
