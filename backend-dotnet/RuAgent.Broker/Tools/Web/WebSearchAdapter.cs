using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RuAgent.Broker.Configuration;

namespace RuAgent.Broker.Tools.Web;

public sealed class WebSearchAdapter
{
    private static readonly Regex GoogleResultPattern = new(
        "<a[^>]+href=[\\\"'](?<url>/url\\?q=[^\\\"']+)[\\\"'][^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex LinkPattern = new(
        "<a[^>]+href=[\\\"'](?<url>https?://[^\\\"']+)[\\\"'][^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex ResultLinkPattern = new(
        "<a\\b(?=[^>]*\\bclass=[\\\"'][^\\\"']*\\bresult__a\\b[^\\\"']*[\\\"'])[^>]*\\bhref=[\\\"'](?<url>[^\\\"']+)[\\\"'][^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex BingResultPattern = new(
        "<li[^>]*\\bclass=\"[^\"]*\\bb_algo\\b[^\"]*\"[^>]*>.*?<a[^>]+href=[\\\"'](?<url>https?://[^\\\"']+)[\\\"'][^>]*>(?<title>.*?)</a>(?:.*?<p>(?<snippet>.*?)</p>)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex DuckDuckGoSnippetPattern = new(
        "<a[^>]*\\bclass=[\\\"'][^\\\"']*\\bresult__snippet\\b[^\\\"']*[\\\"'][^>]*>(?<snippet>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex GoogleSnippetPattern = new(
        "<span[^>]*\\bclass=[\\\"']st[\\\"'][^>]*>(?<snippet>.*?)</span>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex MojeekResultPattern = new(
        "<a[^>]*\\bclass=\"[^\"]*\\btitle\\b[^\"]*\"[^>]*\\bhref=[\\\"'](?<url>[^\\\"']+)[\\\"'][^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly string[] GoogleUserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
    ];
    private readonly HttpClient _http;
    private readonly WebPolicy _policy;
    private readonly Uri _endpoint;
    private readonly IReadOnlyList<Uri> _fallbackEndpoints;
    private readonly int _maxResponseBytes;

    public WebSearchAdapter(
        HttpClient http,
        WebPolicy policy,
        WebContentSanitizer sanitizer,
        Uri endpoint,
        int maxResponseBytes = WebOptions.DefaultMaxResponseBytes,
        IReadOnlyList<Uri>? fallbackEndpoints = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ArgumentNullException.ThrowIfNull(sanitizer);
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _fallbackEndpoints = fallbackEndpoints ?? Array.Empty<Uri>();
        if (maxResponseBytes is < 1 or > WebOptions.MaxConfiguredResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
        }

        _maxResponseBytes = maxResponseBytes;
    }

    public Task<IReadOnlyList<SourceCitation>> SearchAsync(string query, CancellationToken cancellationToken) =>
        SearchAsync(query, 10, cancellationToken);

    public async Task<IReadOnlyList<SourceCitation>> SearchAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 512)
        {
            throw new ArgumentException("search_query_invalid", nameof(query));
        }

        maxResults = Math.Clamp(maxResults, 1, 10);
        // Fixed engine set: the configured primary first, then the built-in
        // free engines. Every request still passes WebPolicy (redirect and
        // private-host validation), so a fallback never widens the network
        // boundary the call was granted under.
        //
        // Engines race: a slow or blocked primary no longer holds the call
        // hostage while a fallback already has usable results. The first
        // engine with usable results wins; empty or failed engines are
        // skipped. When every engine fails, the earliest engine's error is
        // rethrown so the failure stays deterministic.
        var engines = Engines().ToArray();
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tasks = new Task<EngineOutcome>[engines.Length];
        for (var index = 0; index < engines.Length; index++)
        {
            tasks[index] = RunEngineAsync(engines[index], index, query, maxResults, race.Token, cancellationToken);
        }

        try
        {
            var pending = new HashSet<Task<EngineOutcome>>(tasks);
            var errors = new Exception?[engines.Length];
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(done);
                // RunEngineAsync only throws when the caller itself cancelled;
                // engine failures travel as Error and keep the race going.
                var outcome = await done.ConfigureAwait(false);
                if (outcome.Error is not null)
                {
                    errors[outcome.Index] ??= outcome.Error;
                    continue;
                }

                if (outcome.Results is { Count: > 0 })
                {
                    return outcome.Results;
                }
            }

            for (var index = 0; index < errors.Length; index++)
            {
                if (errors[index] is not null)
                {
                    throw errors[index]!;
                }
            }

            // Unreachable while SearchEngineAsync fails closed on an empty
            // parse; kept as an explicit failure so the contract cannot drift
            // back to a silent empty result.
            throw new InvalidDataException("web_search_parse_failed");
        }
        finally
        {
            // Stop the losers; observe them so a faulted loser never escapes
            // as an unobserved task exception after the winner returned.
            race.Cancel();
            foreach (var task in tasks)
            {
                _ = task.ContinueWith(
                    static settled => _ = settled.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    private sealed record EngineOutcome(int Index, IReadOnlyList<SourceCitation>? Results, Exception? Error);

    private async Task<EngineOutcome> RunEngineAsync(
        Uri endpoint,
        int index,
        string query,
        int maxResults,
        CancellationToken raceToken,
        CancellationToken callerToken)
    {
        try
        {
            var results = await SearchEngineAsync(endpoint, query, maxResults, raceToken).ConfigureAwait(false);
            return new EngineOutcome(index, results, null);
        }
        catch (Exception error) when (error is InvalidDataException or HttpRequestException or UnauthorizedAccessException
            || (error is TaskCanceledException && !callerToken.IsCancellationRequested))
        {
            // Engine failure (block page, layout change, timeout, denied
            // host): the race continues with the remaining engines. User
            // cancellation still throws immediately.
            return new EngineOutcome(index, null, error);
        }
    }

    private IEnumerable<Uri> Engines()
    {
        yield return _endpoint;
        foreach (var fallback in _fallbackEndpoints)
        {
            if (!Uri.Equals(fallback, _endpoint))
            {
                yield return fallback;
            }
        }
    }

    private async Task<IReadOnlyList<SourceCitation>> SearchEngineAsync(
        Uri endpoint,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var queryPart = "q=" + Uri.EscapeDataString(query);
        var existingQuery = endpoint.Query.TrimStart('?');
        var current = new UriBuilder(endpoint)
        {
            Query = string.IsNullOrEmpty(existingQuery)
                ? queryPart
                : existingQuery + "&" + queryPart,
        }.Uri;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            await _policy.ValidateAsync(current, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            ApplyBrowserHeaders(request, endpoint);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                if (redirect == 5)
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
            var htmlResultAnchorCount = isJson ? 0 : CountResultAnchors(body, endpoint);
            var candidates = isJson
                ? ParseJsonCandidates(body)
                : ParseHtmlCandidates(body, endpoint);
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
                if (results.Count == maxResults)
                {
                    break;
                }
            }

            // An unreadable layout is a failure, never an empty answer: the tool
            // description and agent-tools.md both promise a bounded error
            // instead of a silent empty result, and a consent or block page has
            // no result anchors at all.
            if (results.Count == 0)
            {
                throw new InvalidDataException("web_search_parse_failed");
            }

            return results;
        }

        throw new InvalidDataException("web_redirect_limit");
    }

    private static void ApplyBrowserHeaders(HttpRequestMessage request, Uri endpoint)
    {
        if (endpoint.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", GoogleUserAgents[0]);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        }
        else if (endpoint.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase)
            || endpoint.Host.EndsWith("mojeek.com", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", GoogleUserAgents[0]);
        }
    }

    private static int CountResultAnchors(string html, Uri endpoint)
    {
        if (IsGoogleEndpoint(endpoint))
        {
            return GoogleResultPattern.Matches(html).Count;
        }

        if (IsBingEndpoint(endpoint))
        {
            return BingResultPattern.Matches(html).Count;
        }

        if (IsMojeekEndpoint(endpoint))
        {
            return MojeekResultPattern.Matches(html).Count;
        }

        return ResultLinkPattern.Matches(html).Count;
    }

    private static bool IsGoogleEndpoint(Uri endpoint) =>
        endpoint.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsBingEndpoint(Uri endpoint) =>
        endpoint.Host.EndsWith("bing.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsMojeekEndpoint(Uri endpoint) =>
        endpoint.Host.EndsWith("mojeek.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsDuckDuckGoEndpoint(Uri endpoint) =>
        endpoint.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<SearchCandidate> ParseHtmlCandidates(string html, Uri endpoint)
    {
        if (IsGoogleEndpoint(endpoint))
        {
            var google = GoogleResultPattern.Matches(html);
            var googleSnippets = GoogleSnippetPattern.Matches(html);
            return google
                .Select((match, index) => new SearchCandidate(
                    WebUtility.HtmlDecode(match.Groups["url"].Value.Trim()),
                    WebUtility.HtmlDecode(match.Groups["title"].Value.Trim()),
                    index < googleSnippets.Count
                        ? WebUtility.HtmlDecode(googleSnippets[index].Groups["snippet"].Value.Trim())
                        : null))
                .ToArray();
        }

        if (IsBingEndpoint(endpoint))
        {
            var bing = BingResultPattern.Matches(html)
                .Select(match => new SearchCandidate(
                    WebUtility.HtmlDecode(match.Groups["url"].Value.Trim()),
                    WebUtility.HtmlDecode(match.Groups["title"].Value.Trim()),
                    match.Groups["snippet"].Success
                        ? WebUtility.HtmlDecode(match.Groups["snippet"].Value.Trim())
                        : null))
                .ToArray();
            if (bing.Length > 0)
            {
                return bing;
            }
        }

        if (IsDuckDuckGoEndpoint(endpoint))
        {
            var links = ResultLinkPattern.Matches(html);
            if (links.Count > 0)
            {
                var duckSnippets = DuckDuckGoSnippetPattern.Matches(html);
                return links
                    .Select((match, index) => new SearchCandidate(
                        WebUtility.HtmlDecode(match.Groups["url"].Value.Trim()),
                        WebUtility.HtmlDecode(match.Groups["title"].Value.Trim()),
                        index < duckSnippets.Count
                            ? WebUtility.HtmlDecode(duckSnippets[index].Groups["snippet"].Value.Trim())
                            : null))
                    .ToArray();
            }
        }

        if (IsMojeekEndpoint(endpoint))
        {
            var mojeek = MojeekResultPattern.Matches(html)
                .Select(match => new SearchCandidate(
                    WebUtility.HtmlDecode(match.Groups["url"].Value.Trim()),
                    WebUtility.HtmlDecode(match.Groups["title"].Value.Trim()),
                    null))
                .ToArray();
            if (mojeek.Length > 0)
            {
                return mojeek;
            }
        }

        return ParseGenericHtmlCandidates(html);
    }

    private static IReadOnlyList<SearchCandidate> ParseHtmlCandidates(string html) =>
        ParseGenericHtmlCandidates(html);

    private static IReadOnlyList<SearchCandidate> ParseGenericHtmlCandidates(string html)
    {
        var resultLinks = ResultLinkPattern.Matches(html);
        var links = resultLinks.Count > 0 ? resultLinks : LinkPattern.Matches(html);
        return links
            .Select(match => new SearchCandidate(
                WebUtility.HtmlDecode(match.Groups["url"].Value.Trim()),
                WebUtility.HtmlDecode(match.Groups["title"].Value.Trim()),
                null))
            .ToArray();
    }

    private static bool TryResolveCandidateUrl(Uri responseUri, string rawUrl, out Uri url)
    {
        url = null!;
        var decoded = WebUtility.HtmlDecode(rawUrl).Trim();
        if (decoded.StartsWith("/url?q=", StringComparison.OrdinalIgnoreCase))
        {
            var target = ReadQueryValue(decoded[(decoded.IndexOf('?') + 1)..], "q");
            if (target is null
                || !Uri.TryCreate(target, UriKind.Absolute, out var google)
                || google.Scheme is not ("http" or "https"))
            {
                return false;
            }

            url = google;
            return true;
        }
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
