using System.Text.RegularExpressions;

namespace RuBackend.Harness.Subagents;

/// <summary>
/// Provenance for a detached child (ADR 0045). The parent quotes the user's own words asking
/// for background work; the Host checks that the quote occurs, as whole words in order, in the
/// authenticated root prompt. Like a model override, this proves where the words came from,
/// not what they mean: the parent still decides whether the user actually asked for it.
/// </summary>
public static class SubagentDetachedRequest
{
    // A single word or a few letters could match almost any prompt.
    private const int MinimumWords = 2;
    private const int MinimumSignificantCharacters = 6;

    public static bool OccursInPrompt(string prompt, string? quote)
    {
        if (string.IsNullOrWhiteSpace(quote) || quote.Any(char.IsControl)) return false;
        var needle = Words(quote);
        if (needle.Length < MinimumWords || needle.Sum(w => w.Length) < MinimumSignificantCharacters) return false;
        var haystack = Words(prompt);
        for (var start = 0; start + needle.Length <= haystack.Length; start++)
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle)) return true;
        return false;
    }

    // Case, whitespace and punctuation never decide provenance; whole words and their order do.
    private static string[] Words(string value) =>
        Regex.Matches(value.Normalize(System.Text.NormalizationForm.FormC), @"[\p{L}\p{M}\p{N}]+",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            .Select(m => m.Value.ToLowerInvariant()).ToArray();
}
