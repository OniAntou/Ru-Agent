namespace RuApp.Services;

/// <summary>
/// Cuts display text at a UTF-16 budget without splitting a surrogate pair.
/// </summary>
/// <remarks>
/// Truncating at an arbitrary index can keep the high half of an astral-plane
/// character and drop its low half; the resulting string holds an unpaired
/// surrogate, which renders as the replacement glyph in front of the ellipsis.
/// </remarks>
internal static class TextTruncation
{
    /// <summary>
    /// At most <paramref name="length"/> UTF-16 units, shortened by one when the
    /// budget would fall between the halves of a surrogate pair.
    /// </summary>
    internal static string TakeAtMost(string text, int length)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (length <= 0) return string.Empty;
        if (text.Length <= length) return text;
        int cut = char.IsHighSurrogate(text[length - 1]) ? length - 1 : length;
        return text[..cut];
    }
}
