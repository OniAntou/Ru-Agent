using System.Text;

namespace RuApp.Tests;

/// <summary>
/// The product renders no emoji, including in the dingbat range that can read as
/// ordinary typography. Shared so the rule is defined once and applied to every
/// surface that ships presentation strings.
/// </summary>
internal static class EmojiPolicy
{
    public static bool IsEmojiRune(Rune rune)
    {
        var value = rune.Value;
        return value is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139
            or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x20E3 or 0xFE0F
            or >= 0x2194 and <= 0x21FF
            or >= 0x2300 and <= 0x23FF
            or >= 0x2600 and <= 0x27BF
            or >= 0x2934 and <= 0x2935
            or >= 0x2B00 and <= 0x2BFF
            or >= 0x1F000 and <= 0x1FAFF
            or >= 0xE0020 and <= 0xE007F;
    }

    /// <summary>Attributes whose value reaches the screen.</summary>
    public static bool IsRenderedTextAttribute(string attributeLocalName) =>
        attributeLocalName is "Text" or "Content" or "Header" or "PlaceholderText"
            or "Description" or "ToolTipService.ToolTip";

    /// <summary>
    /// Extracts the contents of simple string literals. Good enough for a scan: a
    /// miss in an exotic literal form is caught by the markup assertions, while a
    /// false positive here would fail loudly.
    /// </summary>
    public static IEnumerable<string> StringLiterals(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] != '"' || (index > 0 && source[index - 1] == '\\'))
            {
                continue;
            }

            int end = source.IndexOf('"', index + 1);
            if (end < 0)
            {
                yield break;
            }

            yield return source[(index + 1)..end];
            index = end;
        }
    }

    /// <summary>Every emoji scalar found in a rendered-text attribute value.</summary>
    public static IEnumerable<Rune> FindIn(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        foreach (var rune in value.EnumerateRunes())
        {
            if (IsEmojiRune(rune))
            {
                yield return rune;
            }
        }
    }
}
