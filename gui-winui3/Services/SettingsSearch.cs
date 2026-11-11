namespace RuApp.Services;

/// <summary>
/// The matching rule behind the Settings search box: every word the reader typed
/// must appear somewhere in an entry's text, in any order, ignoring case. Kept free
/// of UI types so the rule is tested without a window.
/// </summary>
internal static class SettingsSearch
{
    /// <summary>The words of a query; empty when the reader typed nothing but spaces.</summary>
    public static string[] Words(string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? []
            : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True when the query is empty or every word occurs in <paramref name="text"/>.</summary>
    public static bool Matches(string? query, string? text)
    {
        var words = Words(query);
        if (words.Length == 0)
        {
            return true;
        }

        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (var word in words)
        {
            if (!text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
