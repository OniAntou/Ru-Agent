using System.Text;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Bounded retention for one output stream: keeps the first <c>headCapacity</c> and the
/// last <c>tailCapacity</c> characters, counts everything dropped in between, and renders
/// an explicit truncation marker instead of pretending nothing was cut.
/// </summary>
public sealed class HeadTailOutputBuffer
{
    private readonly StringBuilder _head = new();
    private readonly StringBuilder _tail = new();

    public HeadTailOutputBuffer(int headCapacity, int tailCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(headCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(tailCapacity);
        HeadCapacity = headCapacity;
        TailCapacity = tailCapacity;
    }

    public int HeadCapacity { get; }

    public int TailCapacity { get; }

    public long TotalChars { get; private set; }

    public long TotalBytes { get; private set; }

    public long DroppedChars { get; private set; }

    public long DroppedBytes { get; private set; }

    public string Head => _head.ToString();

    public string Tail => _tail.ToString();

    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
        {
            return;
        }

        TotalChars += text.Length;
        TotalBytes += Encoding.UTF8.GetByteCount(text);

        if (_head.Length < HeadCapacity)
        {
            var taken = Math.Min(HeadCapacity - _head.Length, text.Length);
            _head.Append(text, 0, taken);
            text = text[taken..];
        }

        if (text.Length == 0)
        {
            return;
        }

        _tail.Append(text);
        if (_tail.Length > TailCapacity)
        {
            var excess = _tail.Length - TailCapacity;
            var dropped = _tail.ToString(0, excess);
            DroppedChars += excess;
            DroppedBytes += Encoding.UTF8.GetByteCount(dropped);
            _tail.Remove(0, excess);
        }
    }

    public string Render()
    {
        if (_tail.Length == 0)
        {
            return _head.ToString();
        }

        return DroppedChars == 0
            ? _head.ToString() + _tail.ToString()
            : $"{_head}\n[{DroppedChars} chars truncated]\n{_tail}";
    }
}
