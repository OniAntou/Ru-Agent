using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RuAgent.Broker.Configuration;

namespace RuAgent.Broker.Tools.Web;

public sealed class WebSearchAdapter
{
    private static readonly string[] GoogleUserAgents =
    [
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
    ];
    private readonly HttpClient _http;
    private readonly WebPolicy _policy;
    private readonly Uri _endpoint;
    private readonly IReadOnlyList<Uri> _fallbackEndpoints;
    private readonly int _maxResponseBytes;
    private readonly WebSearchMode _searchMode;

    public WebSearchAdapter(
        HttpClient http,
        WebPolicy policy,
        WebContentSanitizer sanitizer,
        Uri endpoint,
        int maxResponseBytes = WebOptions.DefaultMaxResponseBytes,
        IReadOnlyList<Uri>? fallbackEndpoints = null,
        WebSearchMode searchMode = WebSearchMode.HtmlRace)
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
        if (!Enum.IsDefined(searchMode)) throw new ArgumentOutOfRangeException(nameof(searchMode));
        _searchMode = searchMode;
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
        if (_searchMode == WebSearchMode.ConfiguredJson)
            return await SearchEngineAsync(_endpoint, query, maxResults, cancellationToken).ConfigureAwait(false);
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
            if (_searchMode == WebSearchMode.ConfiguredJson)
            {
                request.Headers.Accept.Clear();
                request.Headers.Accept.ParseAdd("application/json");
            }
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

            var body = await WebResponseReader.ReadAsync(response, _maxResponseBytes, cancellationToken).ConfigureAwait(false);
            var isJson = contentType.Contains("json", StringComparison.OrdinalIgnoreCase);
            if (_searchMode == WebSearchMode.ConfiguredJson && !isJson)
                throw new InvalidDataException("web_search_json_required");
            var candidates = isJson
                ? ParseJsonCandidates(body)
                : WebSearchHtmlParser.Parse(body, endpoint).Select(candidate => new SearchCandidate(candidate.Url, candidate.Title, candidate.Snippet)).ToArray();
            var results = new List<SourceCitation>();
            var seenUrls = new HashSet<string>(StringComparer.Ordinal);
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

            var relevant = WebSearchRelevance.Filter(endpoint, query, results);
            if (relevant.Count == 0) throw new InvalidDataException("web_search_irrelevant_results");
            return relevant;
        }

        throw new InvalidDataException("web_redirect_limit");
    }

    private static void ApplyBrowserHeaders(HttpRequestMessage request, Uri endpoint)
    {
        if (WebSearchHtmlParser.IsEngine(endpoint, "google.com"))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", GoogleUserAgents[0]);
            request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        }
        else if (WebSearchHtmlParser.IsEngine(endpoint, "bing.com")
            || WebSearchHtmlParser.IsEngine(endpoint, "mojeek.com"))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", GoogleUserAgents[0]);
        }
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

        if (WebSearchHtmlParser.IsEngine(candidate, "duckduckgo.com")
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

        if (WebSearchHtmlParser.IsEngine(candidate, "bing.com") && candidate.AbsolutePath == "/ck/a")
        {
            var target = ReadQueryValue(candidate.Query, "u");
            if (target is null) return false;
            if (target.StartsWith("a1", StringComparison.Ordinal))
            {
                try
                {
                    var encoded = target[2..].Replace('-', '+').Replace('_', '/');
                    encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                    target = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(encoded));
                }
                catch (Exception error) when (error is FormatException or DecoderFallbackException)
                {
                    return false;
                }
            }
            if (!Uri.TryCreate(target, UriKind.Absolute, out candidate)
                || candidate.Scheme is not ("http" or "https")) return false;
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
                        ?? ReadJsonString(item, "content")
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
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<JsonElement>();
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

    private static string Bound(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
