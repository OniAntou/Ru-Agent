using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Broker.Tools.Web;

/// <summary>Rejects obvious off-query engine pages, not a semantic relevance scorer.</summary>
internal static class WebSearchRelevance
{
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "what", "how", "are", "today", "latest",
        "hom", "nay", "cho", "anh", "hay", "cua", "cac", "nhung", "nhat", "biet",
    };

    public static IReadOnlyList<SourceCitation> Filter(Uri engine, string query, IReadOnlyList<SourceCitation> results)
    {
        if (!new[] { "google.com", "bing.com", "duckduckgo.com", "mojeek.com" }
            .Any(domain => WebSearchHtmlParser.IsEngine(engine, domain))) return results;
        var sites = Regex.Matches(query, @"(?i)\bsite:([a-z0-9.-]+)")
            .Select(match => match.Groups[1].Value.TrimEnd('.')).ToArray();
        var text = Regex.Replace(query, @"(?i)\bsite:[a-z0-9.-]+", " ");
        var terms = Regex.Matches(Fold(text), @"[\p{L}\p{N}]{2,}")
            .Select(match => match.Value).Where(term => !Noise.Contains(term)).Distinct().Take(32).ToArray();
        if (terms.Length == 0 && sites.Length == 0) return results;
        return results.Where(result =>
            (sites.Length == 0 || sites.Any(site => result.Url.Host.Equals(site, StringComparison.OrdinalIgnoreCase)
                || result.Url.Host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase)))
            && (terms.Length == 0 || terms.Any(term => Fold(result.Title + " " + result.Snippet + " "
                + Uri.UnescapeDataString(result.Url.AbsoluteUri)).Contains(term, StringComparison.Ordinal))))
            .ToArray();
    }

    private static string Fold(string value)
    {
        var normalized = value.ToLowerInvariant().Replace('đ', 'd').Normalize(NormalizationForm.FormD);
        return string.Concat(normalized.Where(character => CharUnicodeInfo.GetUnicodeCategory(character)
            != UnicodeCategory.NonSpacingMark));
    }
}
