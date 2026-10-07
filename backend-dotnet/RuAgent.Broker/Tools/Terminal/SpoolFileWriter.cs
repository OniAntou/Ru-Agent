using System.Text;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Streams one terminal task's redacted frames into a session-scoped spool
/// file once the in-memory window fills, so the swallowed middle is never
/// lost. The window keeps the head for receipts and live paging; the file
/// carries the rest up to the retention ceiling. Frames are already redacted
/// and owned by the session that produced them.
/// </summary>
public sealed class SpoolFileWriter
{
    private readonly string _filePath;
    private readonly int _maxChars;
    private readonly StringBuilder _window = new();
    private StreamWriter? _file;
    private bool _hasFile;
    private int _recorded;
    private bool _truncated;

    public SpoolFileWriter(string filePath, int maxChars = Protocol.TerminalLimits.MaxSpoolFileBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);
        _filePath = filePath;
        _maxChars = maxChars;
    }

    public string Window => _window.ToString();

    public string? FilePath => _hasFile ? _filePath : null;

    public int TotalChars => _recorded;

    public bool Truncated => _truncated;

    public void Append(string frame)
    {
        if (string.IsNullOrEmpty(frame) || _truncated)
        {
            return;
        }

        var remaining = _maxChars - _recorded;
        if (remaining <= 0)
        {
            _truncated = true;
            return;
        }

        if (frame.Length > remaining)
        {
            frame = frame[..remaining];
            _truncated = true;
        }

        var windowRoom = Protocol.TerminalLimits.MaxSpoolChars - _window.Length;
        if (_file is null && frame.Length <= windowRoom)
        {
            _window.Append(frame);
            _recorded += frame.Length;
            return;
        }

        if (_file is null && !_hasFile)
        {
            StartFile();
        }

        if (_file is null)
        {
            // The spill file cannot open: keep only what still fits the window
            // (bounded), never fail the command over retention.
            var kept = Math.Min(windowRoom, frame.Length);
            _window.Append(frame, 0, kept);
            _recorded += kept;
            return;
        }

        var buffered = Math.Min(windowRoom, frame.Length);
        _window.Append(frame, 0, buffered);
        if (frame.Length > buffered)
        {
            _file.Write(frame.AsSpan(buffered));
        }

        _recorded += frame.Length;
    }

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
