using System.Text;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Streams one terminal task's redacted frames into a session-scoped spool
/// file once the in-memory window fills, so the swallowed middle is never
/// lost. The window keeps the head for receipts and live paging; the file
/// carries the rest up to the retention ceiling. The ceiling counts UTF-8
/// bytes because that is the unit the runner's output limit uses; frames are
/// already redacted and owned by the session that produced them.
/// </summary>
public sealed class SpoolFileWriter
{
    private readonly string _filePath;
    private readonly int _maxBytes;
    private readonly SpoolByteBudget? _budget;
    private readonly StringBuilder _window = new();
    private StreamWriter? _file;
    private bool _hasFile;
    private int _recorded;
    private long _recordedBytes;
    private bool _truncated;

    public SpoolFileWriter(
        string filePath,
        int maxBytes = Protocol.TerminalLimits.MaxSpoolFileBytes,
        SpoolByteBudget? budget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _filePath = filePath;
        _maxBytes = maxBytes;
        _budget = budget;
    }

    public string Window => _window.ToString();

    public string? FilePath => _hasFile ? _filePath : null;

    public int TotalChars => _recorded;

    /// <summary>UTF-8 bytes retained for this output (window plus spill file).</summary>
    public long TotalBytes => _recordedBytes;

    public bool Truncated => _truncated;

    public void Append(string frame)
    {
        if (string.IsNullOrEmpty(frame) || _truncated)
        {
            return;
        }

        var frameBytes = Encoding.UTF8.GetByteCount(frame);
        var remaining = _maxBytes - _recordedBytes;
        if (remaining <= 0)
        {
            _truncated = true;
            return;
        }

        if (frameBytes > remaining)
        {
            frame = TakeUtf8Prefix(frame, (int)remaining);
            _truncated = true;
            frameBytes = Encoding.UTF8.GetByteCount(frame);
            if (frame.Length == 0)
            {
                return;
            }
        }

        var windowRoom = Protocol.TerminalLimits.MaxSpoolChars - _window.Length;
        if (_file is null && frame.Length <= windowRoom)
        {
            _window.Append(frame);
            _recorded += frame.Length;
            _recordedBytes += frameBytes;
            return;
        }

        if (_file is null && !_hasFile)
        {
            StartFile();
        }

        if (_file is null)
        {
            // The spill file cannot open: keep only what still fits the window
            // (bounded), never fail the command over retention. The dropped tail
            // is truncation, and the receipt must say so instead of reporting a
            // complete capture.
            var kept = Math.Min(windowRoom, frame.Length);
            _window.Append(frame, 0, kept);
            _recorded += kept;
            _recordedBytes += kept == frame.Length
                ? frameBytes
                : Encoding.UTF8.GetByteCount(frame.AsSpan(0, kept));
            _truncated = true;
            return;
        }

        var buffered = Math.Min(windowRoom, frame.Length);
        _window.Append(frame, 0, buffered);
        var recorded = frame.Length;
        if (frame.Length > buffered)
        {
            var spill = frame.AsSpan(buffered);
            if (_budget is not null)
            {
                var spillBytes = Encoding.UTF8.GetByteCount(spill);
                var granted = _budget.TryReserve(spillBytes);
                if (granted < spillBytes)
                {
                    // The session quota is spent: stop spilling and report the
                    // dropped tail honestly rather than growing past the budget.
                    _truncated = true;
                    spill = granted <= 0
                        ? ReadOnlySpan<char>.Empty
                        : TakeUtf8Prefix(spill.ToString(), (int)granted).AsSpan();
                    recorded = buffered + spill.Length;
                }
            }

            if (!spill.IsEmpty)
            {
                _file.Write(spill);
            }
        }

        _recorded += recorded;
        _recordedBytes += recorded == frame.Length
            ? frameBytes
            : Encoding.UTF8.GetByteCount(frame.AsSpan(0, recorded));
    }

    /// <summary>
    /// Drops this writer's live claim on the session byte quota. Called after
    /// the finished spool is stored, so the bytes move from live to stored
    /// accounting instead of being counted twice.
    /// </summary>
    public void ReleaseBudget() => _budget?.Release();

    public void Complete()
    {
        if (_file is null)
        {
            return;
        }

        try
        {
            _file.Dispose();
        }
        catch (IOException)
        {
        }

        _file = null;
    }

    private static string TakeUtf8Prefix(string text, int maxBytes)
    {
        if (maxBytes <= 0 || text.Length == 0)
        {
            return string.Empty;
        }

        var chars = 0;
        var usedBytes = 0;
        while (chars < text.Length)
        {
            var next = chars + 1;
            if (char.IsHighSurrogate(text[chars])
                && next < text.Length
                && char.IsLowSurrogate(text[next]))
            {
                next++;
            }

            var candidateBytes = Encoding.UTF8.GetByteCount(text.AsSpan(chars, next - chars));
            if (usedBytes + candidateBytes > maxBytes)
            {
                break;
            }

            usedBytes += candidateBytes;
            chars = next;
        }

        return text[..chars];
    }

    private void StartFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            _file = new StreamWriter(
                _filePath,
                append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            _hasFile = true;
        }
        catch (IOException)
        {
            _file = null;
        }
        catch (UnauthorizedAccessException)
        {
            _file = null;
        }
    }
}
