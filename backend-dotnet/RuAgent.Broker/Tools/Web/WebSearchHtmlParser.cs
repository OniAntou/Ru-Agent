using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace RuAgent.Broker.Tools.Web;

internal static class WebSearchHtmlParser
{
    internal sealed record Candidate(string Url, string Title, string? Snippet);

    public static bool IsEngine(Uri endpoint, string domain) =>
        endpoint.Host.Equals(domain, StringComparison.OrdinalIgnoreCase)
        || endpoint.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<Candidate> Parse(string html, Uri endpoint)
    {
        // No resource loader or script engine: parse only the downloaded document.
        using var document = new HtmlParser().ParseDocument(html);
        if (IsEngine(endpoint, "bing.com"))
            return document.QuerySelectorAll("li.b_algo")
                .Select(block => FromLink(block.QuerySelector("h2 a[href]"), block.QuerySelector("p")?.TextContent))
                .OfType<Candidate>().ToArray();
        if (IsEngine(endpoint, "mojeek.com"))
            return document.QuerySelectorAll("a.title[href]")
                .Select(link => FromLink(link, link.Closest("li")?.QuerySelector("p")?.TextContent))
                .OfType<Candidate>().ToArray();
        if (IsEngine(endpoint, "duckduckgo.com"))
        {
            var snippets = document.QuerySelectorAll(".result__snippet");
            return document.QuerySelectorAll("a.result__a[href]")
                .Select((link, index) => FromLink(link,
                    link.Closest(".result")?.QuerySelector(".result__snippet")?.TextContent
                    ?? (index < snippets.Length ? snippets[index].TextContent : null)))
                .OfType<Candidate>().ToArray();
        }
        if (IsEngine(endpoint, "google.com"))
        {
            var snippets = document.QuerySelectorAll("span.st");
            return document.QuerySelectorAll("a[href]")
                .Where(link => link.QuerySelector("h3") is not null
                    || (link.GetAttribute("href") ?? "").StartsWith("/url?q=", StringComparison.Ordinal))
                .Select((link, index) => FromLink(link,
                    index < snippets.Length ? snippets[index].TextContent : null,
                    link.QuerySelector("h3")?.TextContent))
                .OfType<Candidate>().ToArray();
        }
        // Custom endpoints retain the generic HTML contract. Known engines never
        // fall back to arbitrary consent, CAPTCHA or navigation anchors.
        var resultLinks = document.QuerySelectorAll("a.result__a[href]");
        var links = resultLinks.Length > 0 ? resultLinks : document.QuerySelectorAll("a[href]");
        return links.Select(link => FromLink(link, null)).OfType<Candidate>().ToArray();
    }

    private static Candidate? FromLink(IElement? link, string? snippet, string? title = null) =>
        link is null ? null : new(link.GetAttribute("href") ?? "", title ?? link.TextContent, snippet);
}
