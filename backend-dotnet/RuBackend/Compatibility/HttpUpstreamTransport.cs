using Microsoft.Extensions.Options;
using RuBackend.Configuration;

namespace RuBackend.Compatibility;

public sealed class HttpUpstreamTransport : IUpstreamHttpTransport
{
    private static readonly HashSet<string> SafeResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cache-Control",
        "ETag",
        "Last-Modified",
        "Location",
        "Retry-After",
    };

    private readonly HttpClient _httpClient;
    private readonly IOptions<BackendOptions> _options;

    public HttpUpstreamTransport(HttpClient httpClient, IOptions<BackendOptions> options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<UpstreamResponse> SendAsync(
        UpstreamRequest request,
        CancellationToken cancellationToken)
    {
        var upstreamBase = new Uri(_options.Value.UpstreamUrl.TrimEnd('/') + "/", UriKind.Absolute);
        var upstreamUri = new Uri(upstreamBase, request.PathAndQuery);

        using var message = new HttpRequestMessage(request.Method, upstreamUri);
        if (request.Body.Length > 0 || request.Headers.ContainsKey("Content-Type"))
        {
            message.Content = new ByteArrayContent(request.Body);
        }

        foreach (var header in request.Headers)
        {
            if (header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                message.Content ??= new ByteArrayContent(request.Body);
                message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                continue;
            }

            message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using var response = await _httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var responseHeaders = response.Headers
            .Concat(response.Content.Headers)
            .Where(header => SafeResponseHeaders.Contains(header.Key))
            .ToDictionary(
                header => header.Key,
                header => string.Join(", ", header.Value),
                StringComparer.OrdinalIgnoreCase);

        return new UpstreamResponse(
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString() ?? string.Empty,
            body,
            responseHeaders);
    }
}
