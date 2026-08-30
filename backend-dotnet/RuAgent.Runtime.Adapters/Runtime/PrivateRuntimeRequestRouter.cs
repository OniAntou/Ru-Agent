using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RuAgent.Runtime.Adapters.Runtime;

public sealed record PrivateRuntimeRequest
{
    public PrivateRuntimeRequest(
        string method,
        string pathAndQuery,
        string? contentType,
        ReadOnlyMemory<byte> body)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        PathAndQuery = pathAndQuery ?? throw new ArgumentNullException(nameof(pathAndQuery));
        ContentType = contentType;
        Body = body;
    }

    public string Method { get; }

    public string PathAndQuery { get; }

    public string? ContentType { get; }

    public ReadOnlyMemory<byte> Body { get; }
}

public sealed record PrivateRuntimeResponse
{
    public PrivateRuntimeResponse(
        int statusCode,
        string contentType,
        ReadOnlyMemory<byte> body,
        IReadOnlyDictionary<string, string>? headers = null,
        bool binary = false)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(nameof(statusCode));
        }

        if (string.IsNullOrWhiteSpace(contentType) || contentType.Length > 256)
        {
            throw new ArgumentException("Runtime response content type is invalid.", nameof(contentType));
        }

        if (body.Length > PrivateRuntimeRequestRouter.MaxBodyBytes)
        {
            throw new ArgumentException("Runtime response body is too large.", nameof(body));
        }

        if (binary && body.IsEmpty)
        {
            throw new ArgumentException("Binary runtime responses require a body.", nameof(body));
        }

        if (headers is not null)
        {
            foreach (var header in headers)
            {
                if (string.IsNullOrWhiteSpace(header.Key)
                    || header.Key.Length > 128
                    || string.IsNullOrWhiteSpace(header.Value)
                    || header.Value.Length > 1_024
                    || header.Key.Any(char.IsControl)
                    || header.Value.Any(char.IsControl))
                {
                    throw new ArgumentException("Runtime response headers are invalid.", nameof(headers));
                }
            }
        }

        StatusCode = statusCode;
        ContentType = contentType.Trim();
        Body = body;
        Headers = headers;
        Binary = binary;
    }

    public int StatusCode { get; }

    public string ContentType { get; }

    public ReadOnlyMemory<byte> Body { get; }

    public IReadOnlyDictionary<string, string>? Headers { get; }

    public bool Binary { get; }
}

public interface IPrivateRuntimeRequestHandler
{
    Task<PrivateRuntimeResponse> InvokeAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken);
}

public sealed class PrivateRuntimeRequestException : Exception
{
    public PrivateRuntimeRequestException(
        string code,
        bool retryable = false,
        Exception? innerException = null)
        : base(code, innerException)
    {
        Code = code;
        Retryable = retryable;
    }

    public string Code { get; }

    public bool Retryable { get; }
}

public sealed class PrivateRuntimeRequestRouter
{
    public const int MaxPathAndQueryChars = 2_048;
    public const int MaxBodyBytes = 8 * 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly IReadOnlySet<(string Method, string Path)> AllowlistedRoutes =
        new HashSet<(string Method, string Path)>
        {
            ("GET", "/status"),
            ("GET", "/models"),
            ("GET", "/voice/status"),
            ("POST", "/voice/latency"),
            ("POST", "/voice/tts"),
            ("POST", "/voice/warmup"),
            ("POST", "/voice/release"),
            ("POST", "/voice/transcribe"),
            ("POST", "/observation/analyze"),
            ("GET", "/vts/mouse-look"),
            ("POST", "/vts/mouse-look"),
            ("GET", "/vts/overlay"),
            ("POST", "/vts/overlay"),
            ("GET", "/vts/status"),
            ("POST", "/vts/connect"),
            ("GET", "/vts/config"),
            ("POST", "/vts/config"),
            ("POST", "/vts/activity"),
            ("POST", "/vts/action"),
            ("POST", "/vts/speaking"),
            ("POST", "/observation/preflight"),
            ("GET", "/observation/vision-config"),
            ("POST", "/observation/react"),
            ("POST", "/observation/cancel"),
            ("POST", "/observation/ack"),
            ("POST", "/observation/start"),
            ("POST", "/observation/stop"),
            ("GET", "/observation/status"),
        };

    private static readonly IReadOnlySet<string> SensitiveKeys = new HashSet<string>(
        [
            "api_key",
            "opencode_api_key",
            "kilo_api_key",
            "gemini_vision_api_key",
            "custom_api_key",
            "token",
            "secret",
            "password",
            "credential",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly IPrivateRuntimeRequestHandler _handler;
    private readonly IReadOnlyDictionary<(string Method, string Path), IPrivateRuntimeRequestHandler> _routeHandlers;

    public PrivateRuntimeRequestRouter(
        IPrivateRuntimeRequestHandler handler,
        IReadOnlyDictionary<(string Method, string Path), IPrivateRuntimeRequestHandler>? routeHandlers = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _routeHandlers = routeHandlers is null
            ? new Dictionary<(string Method, string Path), IPrivateRuntimeRequestHandler>()
            : new Dictionary<(string Method, string Path), IPrivateRuntimeRequestHandler>(routeHandlers);
    }

    public PrivateRuntimeRequestRouter WithRouteHandler(
        string method,
        string path,
        IPrivateRuntimeRequestHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var key = (method.Trim().ToUpperInvariant(), path);
        if (!AllowlistedRoutes.Contains(key))
        {
            throw new ArgumentException("Runtime route is not allowlisted.", nameof(path));
        }

        var routeHandlers = new Dictionary<(string Method, string Path), IPrivateRuntimeRequestHandler>(_routeHandlers)
        {
            [key] = handler,
        };
        return new PrivateRuntimeRequestRouter(_handler, routeHandlers);
    }

    public async Task<PrivateRuntimeResponse> InvokeAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalizedMethod = NormalizeMethod(request.Method);
        var uri = ParseRelativeUri(request.PathAndQuery);
        ValidateContentType(request.ContentType);
        if (request.Body.Length > MaxBodyBytes)
        {
            throw new PrivateRuntimeRequestException("invalid_request");
        }

        // The compatibility dispatcher parses every POST body before selecting
        // a handler, including routes whose handler does not consume payload
        // fields (connect/release/stop). Keep that boundary here so malformed
        // non-JSON text cannot slip through merely because the selected handler
        // is body-agnostic.
        ValidatePostJson(request, normalizedMethod);

        if (!AllowlistedRoutes.Contains((normalizedMethod, uri.AbsolutePath)))
        {
            throw new PrivateRuntimeRequestException("unsupported_route");
        }

        RejectSensitiveJson(request);

        PrivateRuntimeResponse response;
        try
        {
            var handler = _routeHandlers.TryGetValue(
                    (normalizedMethod, uri.AbsolutePath),
                    out var routeHandler)
                ? routeHandler
                : _handler;
            response = await handler.InvokeAsync(
                new PrivateRuntimeRequest(
                    normalizedMethod,
                    request.PathAndQuery,
                    request.ContentType,
                    request.Body),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PrivateRuntimeRequestException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new PrivateRuntimeRequestException(
                "runtime_request_failed",
                retryable: true,
                error);
        }

        ValidateResponse(response);
        return response;
    }

    private static string NormalizeMethod(string method)
    {
        if (string.IsNullOrWhiteSpace(method)
            || method.Length > 16
            || method.Any(char.IsControl))
        {
            throw new PrivateRuntimeRequestException("invalid_request");
        }

        var normalized = method.Trim().ToUpperInvariant();
        return normalized is "GET" or "POST"
            ? normalized
            : throw new PrivateRuntimeRequestException("invalid_request");
    }

    private static Uri ParseRelativeUri(string pathAndQuery)
    {
        if (string.IsNullOrWhiteSpace(pathAndQuery)
            || pathAndQuery.Length > MaxPathAndQueryChars
            || !pathAndQuery.StartsWith("/", StringComparison.Ordinal)
            || pathAndQuery.Contains('\\')
            || pathAndQuery.Contains('#')
            || pathAndQuery.Any(char.IsControl)
            || pathAndQuery.Contains("/../", StringComparison.Ordinal)
            || pathAndQuery.Contains("/./", StringComparison.Ordinal))
        {
            throw new PrivateRuntimeRequestException("invalid_request");
        }

        if (!Uri.TryCreate(
                "http://127.0.0.1" + pathAndQuery,
                UriKind.Absolute,
                out var uri)
            || !string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)
            || uri.AbsolutePath.Length == 0)
        {
            throw new PrivateRuntimeRequestException("invalid_request");
        }

        return uri;
    }

    private static void ValidateContentType(string? contentType)
    {
        if (contentType is null)
        {
            return;
        }

        if (contentType.Length > 256
            || string.IsNullOrWhiteSpace(contentType)
            || contentType.Any(char.IsControl))
        {
            throw new PrivateRuntimeRequestException("invalid_request");
        }
    }

    private static void RejectSensitiveJson(PrivateRuntimeRequest request)
    {
        if (request.Body.IsEmpty)
        {
            return;
        }

        var contentType = request.ContentType?.ToLowerInvariant() ?? string.Empty;
        var looksJson = contentType.Contains("json", StringComparison.Ordinal)
            || StartsWithJsonToken(request.Body.Span);
        if (!looksJson)
        {
            return;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(StrictUtf8.GetString(request.Body.Span));
        }
        catch (Exception error) when (error is DecoderFallbackException or JsonException)
        {
            throw new PrivateRuntimeRequestException("invalid_request", false, error);
        }

        if (ContainsSensitiveKey(root))
        {
            throw new PrivateRuntimeRequestException("invalid_request");
        }
    }

    private static void ValidatePostJson(PrivateRuntimeRequest request, string normalizedMethod)
    {
        if (!string.Equals(normalizedMethod, "POST", StringComparison.Ordinal)
            || request.Body.IsEmpty)
        {
            return;
        }

        try
        {
            var text = StrictUtf8.GetString(request.Body.Span);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // Parsing is deliberately shape-neutral. Individual handlers still
            // validate whether they require an object, array, or scalar fields.
            _ = JsonNode.Parse(text);
        }
        catch (JsonException error)
        {
            throw new PrivateRuntimeRequestException("invalid_request", false, error);
        }
        catch (DecoderFallbackException error)
        {
            throw new PrivateRuntimeRequestException("invalid_request", false, error);
        }
    }

    private static bool StartsWithJsonToken(ReadOnlySpan<byte> body)
    {
        foreach (var value in body)
        {
            if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return value is (byte)'{' or (byte)'[';
        }

        return false;
    }

    private static bool ContainsSensitiveKey(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                if (SensitiveKeys.Contains(property.Key.Trim()))
                {
                    return true;
                }

                if (ContainsSensitiveKey(property.Value))
                {
                    return true;
                }
            }
        }
        else if (node is JsonArray array)
        {
            return array.Any(ContainsSensitiveKey);
        }

        return false;
    }

    private static void ValidateResponse(PrivateRuntimeResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Binary)
        {
            return;
        }

        var contentType = response.ContentType.ToLowerInvariant();
        if (response.Body.IsEmpty || contentType.StartsWith("text/", StringComparison.Ordinal)
            || contentType.Contains("json", StringComparison.Ordinal))
        {
            try
            {
                _ = StrictUtf8.GetString(response.Body.Span);
            }
            catch (DecoderFallbackException error)
            {
                throw new PrivateRuntimeRequestException("invalid_response", false, error);
            }

            return;
        }

        throw new PrivateRuntimeRequestException("invalid_response");
    }
}

public sealed class UnavailablePrivateRuntimeRequestHandler : IPrivateRuntimeRequestHandler
{
    public Task<PrivateRuntimeResponse> InvokeAsync(
        PrivateRuntimeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new PrivateRuntimeResponse(
            503,
            "application/json",
            Encoding.UTF8.GetBytes("{\"status\":\"error\",\"message\":\"Runtime route is unavailable.\"}")));
    }
}
