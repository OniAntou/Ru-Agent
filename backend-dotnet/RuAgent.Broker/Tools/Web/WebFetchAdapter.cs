using System.Net.Http.Headers;

namespace RuAgent.Broker.Tools.Web;

public sealed class WebFetchAdapter
{
    private readonly HttpClient _http;
    private readonly WebPolicy _policy;
    private readonly WebContentSanitizer _sanitizer;

    public WebFetchAdapter(HttpClient http, WebPolicy policy, WebContentSanitizer sanitizer)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
    }

    public async Task<WebFetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        await _policy.ValidateAsync(uri, cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
            && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("web_content_type_not_supported");
        }

        var text = await ReadBoundedAsync(response, 256 * 1024, cancellationToken).ConfigureAwait(false);
        var title = uri.Host;
        return new WebFetchResult(
            new SourceCitation(title, uri),
            _sanitizer.Sanitize(title, uri, text),
            contentType);
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
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
