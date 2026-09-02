using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Broker.Tools.Help;

public sealed record HelpDocsSearchResult(
    string DocumentId,
    string Title,
    string Section,
    string Excerpt,
    bool WasExcerptTruncated);

public sealed class HelpDocsSearch
{
    private static readonly Regex TokenPattern = new("[\\p{L}\\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly IReadOnlyList<BundledHelpSection> _sections;

    public HelpDocsSearch(BundledHelpDocsCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _sections = catalog.Sections;
    }

    public IReadOnlyList<HelpDocsSearchResult> Search(string query, int maxResults, out bool moreResults)
    {
        var normalizedQuery = Normalize(query);
        var queryTokens = Tokens(normalizedQuery).Distinct(StringComparer.Ordinal).ToArray();
        var candidates = _sections
            .Select(section => Rank(section, normalizedQuery, queryTokens))
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderByDescending(item => item.HeadingPhrase)
            .ThenByDescending(item => item.HeadingTokenCount)
            .ThenByDescending(item => item.BodyPhrase)
            .ThenByDescending(item => item.BodyTokenCount)
            .ThenBy(item => item.Section.DocumentOrdinal)
            .ThenBy(item => item.Section.SectionOrdinal)
            .ToArray();

        moreResults = candidates.Length > maxResults;
        return candidates
            .Take(maxResults)
            .Select(item => new HelpDocsSearchResult(
                item.Section.DocumentId,
                item.Section.Title,
                item.Section.Section,
                Excerpt(item.Section.Text, normalizedQuery, queryTokens, out var truncated),
                truncated))
            .ToArray();
    }

    private static Ranked? Rank(BundledHelpSection section, string query, IReadOnlyList<string> tokens)
    {
        var heading = Normalize(section.Section);
        var body = Normalize(section.Text);
        var headingPhrase = heading.Contains(query, StringComparison.Ordinal);
        var bodyPhrase = body.Contains(query, StringComparison.Ordinal);
        var headingTokenCount = tokens.Count(token => heading.Contains(token, StringComparison.Ordinal));
        var bodyTokenCount = tokens.Count(token => body.Contains(token, StringComparison.Ordinal));
        return !headingPhrase && !bodyPhrase && headingTokenCount == 0 && bodyTokenCount == 0
            ? null
            : new Ranked(section, headingPhrase, headingTokenCount, bodyPhrase, bodyTokenCount);
    }

    private static string Excerpt(string text, string query, IReadOnlyList<string> tokens, out bool truncated)
    {
        const int maxChars = 2_000;
        truncated = false;
        if (text.Length <= maxChars)
        {
            return text;
        }

        var normalized = Normalize(text);
        var index = normalized.IndexOf(query, StringComparison.Ordinal);
        if (index < 0)
        {
            index = tokens.Select(token => normalized.IndexOf(token, StringComparison.Ordinal)).Where(value => value >= 0).DefaultIfEmpty(0).Min();
        }

        var start = Math.Max(0, index - maxChars / 3);
        var end = Math.Min(text.Length, start + maxChars);
        if (start > 0)
        {
            var line = text.LastIndexOf('\n', start - 1);
            if (line >= 0) start = line + 1;
        }

        if (end < text.Length)
        {
            var line = text.IndexOf('\n', end);
            if (line >= 0) end = line;
        }

        var excerpt = text[start..end].Trim();
        if (start > 0) excerpt = "…" + excerpt;
        if (end < text.Length) excerpt += "…";
        truncated = true;
        return excerpt;
    }

    private static string Normalize(string value) => value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();

    private static IEnumerable<string> Tokens(string value) => TokenPattern.Matches(value).Select(match => match.Value);

    private sealed record Ranked(BundledHelpSection Section, bool HeadingPhrase, int HeadingTokenCount, bool BodyPhrase, int BodyTokenCount);
}
