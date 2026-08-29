using System.Text.RegularExpressions;

namespace RuAgent.Runtime.Adapters.Audio;

public static partial class VoiceChunker
{
    private const int DefaultMaxChars = 120;

    [GeneratedRegex(@"(?<=[.!?…])(?<!\d)\s+|\n+", RegexOptions.CultureInvariant)]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"\s*\[\[(?:VTS|VOICE_PACE):[^\]]*\]\]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrivateMarker();

    public static string Clean(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return PrivateMarker().Replace(text, " ").Trim();
    }

    public static IReadOnlyList<string> Split(string text, int maxChars = DefaultMaxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maxChars < 20)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars));
        }

        var chunks = new List<string>();
        foreach (var part in SentenceBreak().Split(text.Trim()))
        {
            var sentence = part.Trim();
            while (sentence.Length > maxChars)
            {
                var boundary = sentence.LastIndexOf(' ', Math.Min(maxChars, sentence.Length - 1), maxChars);
                if (boundary <= 0)
                {
                    boundary = sentence.IndexOf(' ', maxChars);
                }

                if (boundary <= 0)
                {
                    boundary = maxChars;
                }

                chunks.Add(sentence[..boundary].Trim());
                sentence = sentence[boundary..].Trim();
            }

            if (sentence.Length > 0)
            {
                chunks.Add(sentence);
            }
        }

        return chunks;
    }
}
