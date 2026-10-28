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

    public async Task<WebFetchResult> FetchAsync(Uri uri, CancellationToken cancellationToken,
        int offset = 0, int limit = WebContentSanitizer.DefaultMaxCharacters)
    {
        if (offset is < 0 or > WebOptions.MaxConfiguredResponseBytes)
            throw new ArgumentException("web_offset_invalid", nameof(offset));
        if (limit is < 1 or > WebContentSanitizer.DefaultMaxCharacters)
            throw new ArgumentException("web_limit_invalid", nameof(limit));
        var current = uri;
        for (var redirect = 0; redirect <= 5; redirect++)
        {
            await _policy.ValidateAsync(current, cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            WebRequestIdentity.Apply(request);
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
            if (IsPdf(contentType))
            {
                var pdf = await WebResponseReader.ReadBytesAsync(response, _maxResponseBytes, cancellationToken).ConfigureAwait(false);
                var (pdfTitle, pdfText) = WebPdfTextExtractor.Extract(pdf, WebOptions.MaxConfiguredResponseBytes, cancellationToken);
                // A scanned PDF parses but holds only images.
                if (string.IsNullOrWhiteSpace(pdfText)) throw new InvalidDataException("web_pdf_unreadable");
                var page = _sanitizer.Extract(pdfTitle ?? current.Segments[^1], current, pdfText, isHtml: false,
                    Math.Min(limit, _maxResponseBytes), offset);
                return new WebFetchResult(new SourceCitation(page.Title, current), page.Text, contentType, offset, page.NextOffset);
            }

            if (!contentType.Contains("html", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("web_content_type_not_supported");
            }

            var text = await WebResponseReader.ReadAsync(response, _maxResponseBytes, cancellationToken).ConfigureAwait(false);
            var extracted = _sanitizer.Extract(current.Host, current, text,
                contentType.Contains("html", StringComparison.OrdinalIgnoreCase),
                Math.Min(limit, _maxResponseBytes), offset);
            return new WebFetchResult(
                new SourceCitation(extracted.Title, current),
                extracted.Text,
                contentType, offset, extracted.NextOffset);
        }

        throw new InvalidDataException("web_redirect_limit");
    }

    private static bool IsPdf(string contentType) =>
        contentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
        || contentType.Equals("application/x-pdf", StringComparison.OrdinalIgnoreCase);
}
