using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RuAgent.Broker.Configuration;

namespace RuAgent.Broker.Tools.Web;

public sealed class WebSearchAdapter
{
    private static readonly Regex LinkPattern = new(
        "<a[^>]+href=[\\\"'](?<url>https?://[^\\\"']+)[\\\"'][^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ResultLinkPattern = new(
        "<a\\b(?=[^>]*\\bclass=[\\\"'][^\\\"']*\\bresult__a\\b[^\\\"']*[\\\"'])[^>]*\\bhref=[\\\"'](?<url>[^\\\"']+)[\\\"'][^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private readonly HttpClient _http;
    private readonly WebPolicy _policy;
    private readonly Uri _endpoint;
    private readonly int _maxResponseBytes;

    public WebSearchAdapter(
        HttpClient http,
        WebPolicy policy,
        WebContentSanitizer sanitizer,
        Uri endpoint,
        int maxResponseBytes = WebOptions.DefaultMaxResponseBytes)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ArgumentNullException.ThrowIfNull(sanitizer);
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        if (maxResponseBytes is < 1 or > WebOptions.MaxConfiguredResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
        }

        _maxResponseBytes = maxResponseBytes;
    }

    public async Task<IReadOnlyList<SourceCitation>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 512)
        {
            throw new ArgumentException("search_query_invalid", nameof(query));
        }

        var queryPart = "q=" + Uri.EscapeDataString(query);
        var existingQuery = _endpoint.Query.TrimStart('?');
        var current = new UriBuilder(_endpoint)
        {
            Query = string.IsNullOrEmpty(existingQuery)
                ? queryPart
                : existingQuery + "&" + queryPart,
        }.Uri;
        for (var redirect = 0; redirect <= 3; redirect++)
        {
            await _policy.ValidateAsync(current, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirect == 3)
                {
                    throw new InvalidDataException("web_redirect_limit");
                }

                var location = response.Headers.Location;
                if (location is null || !Uri.TryCreate(current, location, out var next))
                {
                    throw new InvalidDataException("web_redirect_invalid");
                }

                await _policy.ValidateAsync(next, cancellationToken).ConfigureAwait(false);
                current = next;
                continue;
            }

            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("web_content_type_not_supported");
            }

            var body = await ReadBoundedAsync(response, _maxResponseBytes, cancellationToken).ConfigureAwait(false);
            var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
            var htmlResultAnchorCount = isJson ? 0 : ResultLinkPattern.Matches(body).Count;
            var candidates = isJson
                ? ParseJsonCandidates(body)
                : ParseHtmlCandidates(body);
            var results = new List<SourceCitation>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                if (!TryResolveCandidateUrl(current, candidate.Url, out var url)
                    || !seenUrls.Add(url.AbsoluteUri))
                {
                    continue;
                }

                try
                {
                    await _policy.ValidateAsync(url, cancellationToken).ConfigureAwait(false);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                var title = CleanText(candidate.Title, 256);
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = url.Host;
                }

                var snippet = string.IsNullOrWhiteSpace(candidate.Snippet)
                    ? "Search result"
                    : CleanText(candidate.Snippet, 512);
                results.Add(new SourceCitation(Bound(title, 256), url, snippet));
                if (results.Count == 10)
                {
                    break;
                }
            }

            if (htmlResultAnchorCount > 0 && results.Count == 0)
            {
                throw new InvalidDataException("web_search_parse_failed");
            }

            return results;
        }

        throw new InvalidDataException("web_redirect_limit");
    }

    private static IReadOnlyList<SearchCandidate> ParseHtmlCandidates(string html)
    {
        var resultLinks = ResultLinkPattern.Matches(html);
        var links = resultLinks.Count > 0 ? resultLinks : LinkPattern.Matches(html);
        return links
            .Select(match => new SearchCandidate(
                WebUtility.HtmlDecode(match.Groups["url"].Value),
                match.Groups["title"].Value,
                null))
            .ToArray();
    }

    private static bool TryResolveCandidateUrl(Uri responseUri, string rawUrl, out Uri url)
    {
        url = null!;
        var decoded = WebUtility.HtmlDecode(rawUrl).Trim();
        if (!Uri.TryCreate(responseUri, decoded, out var candidate)
            || candidate.Scheme is not ("http" or "https"))
        {
            return false;
        }

        if (candidate.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.AbsolutePath, "/l/", StringComparison.Ordinal))
        {
            var target = ReadQueryValue(candidate.Query, "uddg");
            if (target is null
                || !Uri.TryCreate(target, UriKind.Absolute, out candidate)
                || candidate.Scheme is not ("http" or "https"))
            {
                return false;
            }
        }

        url = candidate;
        return true;
    }

    private static string? ReadQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var encodedKey = separator < 0 ? pair : pair[..separator];
            if (!string.Equals(Uri.UnescapeDataString(encodedKey), key, StringComparison.Ordinal))
            {
                continue;
            }

            var encodedValue = separator < 0 ? string.Empty : pair[(separator + 1)..];
            return Uri.UnescapeDataString(encodedValue.Replace('+', ' '));
        }

        return null;
    }

    private static IReadOnlyList<SearchCandidate> ParseJsonCandidates(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    MaxDepth = 32,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
            var root = document.RootElement;
            IEnumerable<JsonElement> items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray()
                : EnumerateObjectResults(root);
            var candidates = new List<SearchCandidate>();
            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var url = ReadJsonString(item, "url")
                    ?? ReadJsonString(item, "link")
                    ?? ReadJsonString(item, "href");
                if (string.IsNullOrWhiteSpace(url) || url.Length > 2048)
                {
                    continue;
                }

                candidates.Add(new SearchCandidate(
                    url,
                    ReadJsonString(item, "title") ?? ReadJsonString(item, "name") ?? string.Empty,
                    ReadJsonString(item, "snippet")
                        ?? ReadJsonString(item, "description")
                        ?? ReadJsonString(item, "summary")));
            }

            return candidates;
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("web_json_invalid", error);
        }
    }

    private static IEnumerable<JsonElement> EnumerateObjectResults(JsonElement root)
    {
        foreach (var property in new[] { "results", "items", "data" })
        {
            if (root.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.Array)
            {
                return value.EnumerateArray();
            }
        }

        return Array.Empty<JsonElement>();
    }

    private static string? ReadJsonString(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string CleanText(string value, int max)
    {
        var clean = WebUtility.HtmlDecode(Regex.Replace(value ?? string.Empty, "<[^>]+>", " "));
        clean = Regex.Replace(clean, @"\s+", " ").Trim();
        return Bound(clean, max);
    }

    private sealed record SearchCandidate(string Url, string Title, string? Snippet);

    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response,
        int limit,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > limit)
        {
            throw new InvalidDataException("web_response_too_large");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream(Math.Min(limit, 64 * 1024));
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            total += count;
            if (total > limit)
            {
                throw new InvalidDataException("web_response_too_large");
            }

            await memory.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(memory.ToArray());
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("web_encoding_invalid", error);
        }
    }

    private static string Bound(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
