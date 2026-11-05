namespace RuAgent.Harness.Core;

/// <summary>Length bounds for short model-visible text such as repair hints.</summary>
public static class BoundedText
{
    private const char Ellipsis = '…';

    /// <summary>
    /// Returns <paramref name="text"/> when it fits; otherwise cuts it at the last word
    /// boundary that leaves room for an ellipsis, so the reader can tell it was shortened.
    /// A cut mid-word ("Send the exact schema valu") read like a garbled instruction.
    /// The result never exceeds <paramref name="maximum"/> characters.
    /// </summary>
    public static string ClipAtWord(string text, int maximum)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 2);
        if (text.Length <= maximum) return text;

        var limit = maximum - 1;
        var cut = text.LastIndexOf(' ', limit);
        // A single long token has no usable boundary; cut it at the limit instead.
        if (cut < limit / 2) cut = limit;
        if (char.IsHighSurrogate(text[cut - 1])) cut--;
        return text[..cut].TrimEnd(' ', ',', ';', ':') + Ellipsis;
    }
}
