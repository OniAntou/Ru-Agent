using System.Net.Http.Headers;
using System.Text;
using RuAgent.Broker.Configuration;

namespace RuAgent.Broker.Tools.Web;

public sealed class WebFetchAdapter
{
    private readonly HttpClient _http;
    private readonly WebPolicy _policy;
    private readonly WebContentSanitizer _sanitizer;
    private readonly int _maxResponseBytes;

    public WebFetchAdapter(
        HttpClient http,
        WebPolicy policy,
        WebContentSanitizer sanitizer,
        int maxResponseBytes = WebOptions.DefaultMaxResponseBytes)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _sanitizer = sanitizer ?? throw new ArgumentNullException(nameof(sanitizer));
        if (maxResponseBytes is < 1 or > WebOptions.MaxConfiguredResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResponseBytes));
        }

        _maxResponseBytes = maxResponseBytes;
    }

    public async Task<WebFetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            await _policy.ValidateAsync(current, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
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
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("web_content_type_not_supported");
            }

            var text = await ReadBoundedAsync(response, _maxResponseBytes, cancellationToken).ConfigureAwait(false);
            var title = current.Host;
            return new WebFetchResult(
                new SourceCitation(title, current),
                _sanitizer.Sanitize(title, current, text, Math.Min(256_000, _maxResponseBytes)),
                contentType);
        }

        throw new InvalidDataException("web_redirect_limit");
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, int limit, CancellationToken cancellationToken)
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
            return new System.Text.UTF8Encoding(false, true).GetString(memory.ToArray());
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("web_encoding_invalid", error);
        }
    }
}
