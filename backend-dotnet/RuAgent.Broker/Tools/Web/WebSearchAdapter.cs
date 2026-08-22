using System.Net;
using System.Text.RegularExpressions;

namespace RuAgent.Broker.Tools.Web;

public sealed class WebSearchAdapter
{
    private static readonly Regex LinkPattern = new("<a[^>]+href=[\"'](?<url>https?://[^\"']+)[\"'][^>]*>(?<title>.*?)</a>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private readonly HttpClient _http;
    private readonly WebPolicy _policy;
    private readonly Uri _endpoint;

    public WebSearchAdapter(HttpClient http, WebPolicy policy, WebContentSanitizer sanitizer, Uri endpoint)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        ArgumentNullException.ThrowIfNull(sanitizer);
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public async Task<IReadOnlyList<SourceCitation>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 512)
        {
            throw new ArgumentException("search_query_invalid", nameof(query));
        }

        await _policy.ValidateAsync(_endpoint, cancellationToken).ConfigureAwait(false);
        var uriBuilder = new UriBuilder(_endpoint);
        uriBuilder.Query = "q=" + Uri.EscapeDataString(query);
        using var response = await _http.GetAsync(uriBuilder.Uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var html = await ReadBoundedAsync(response, 256 * 1024, cancellationToken).ConfigureAwait(false);
        var results = new List<SourceCitation>();
        foreach (Match match in LinkPattern.Matches(html))
        {
            if (!Uri.TryCreate(WebUtility.HtmlDecode(match.Groups["url"].Value), UriKind.Absolute, out var url))
            {
                continue;
            }

            results.Add(new SourceCitation(
                WebUtility.HtmlDecode(Regex.Replace(match.Groups["title"].Value, "<[^>]+>", " ")).Trim(),
                url,
                "Search result"));
            if (results.Count == 10)
            {
                break;
            }
        }

        return results;
    }

    private static async Task<string> ReadBoundedAsync(
        HttpResponseMessage response,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[8192];
        var result = new System.Text.StringBuilder();
        while (result.Length < limit)
        {
            var count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            result.Append(buffer, 0, Math.Min(count, limit - result.Length));
        }

        return result.ToString();
    }
}
