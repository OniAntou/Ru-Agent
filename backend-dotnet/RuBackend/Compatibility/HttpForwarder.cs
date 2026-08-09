using System.Text;

namespace RuBackend.Compatibility;

public sealed class HttpForwarder
{
    private const string UnavailableBody = "{\"detail\":\"Backend upstream unavailable\"}";
    private readonly IUpstreamHttpTransport _transport;

    public HttpForwarder(IUpstreamHttpTransport transport)
    {
        _transport = transport;
    }

    public async Task ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var request = await CreateRequestAsync(context, cancellationToken);
        UpstreamResponse response;
        try
        {
            response = await _transport.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException)
        {
            await WriteUnavailableAsync(context);
            return;
        }

        context.Response.StatusCode = response.StatusCode;
        if (!string.IsNullOrWhiteSpace(response.ContentType))
        {
            context.Response.ContentType = response.ContentType;
        }

        if (response.Headers is not null)
        {
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = header.Value;
            }
        }

        context.Response.ContentLength = response.Body.Length;
        await context.Response.Body.WriteAsync(response.Body, cancellationToken);
    }

    private static async Task<UpstreamRequest> CreateRequestAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        await using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, cancellationToken);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.Length > 0)
        {
            headers["Authorization"] = authorization;
        }

        var contentType = context.Request.ContentType;
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            headers["Content-Type"] = contentType;
        }

        return new UpstreamRequest(
            new HttpMethod(context.Request.Method),
            context.Request.PathBase + context.Request.Path + context.Request.QueryString,
            headers,
            body.ToArray());
    }

    private static async Task WriteUnavailableAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength = Encoding.UTF8.GetByteCount(UnavailableBody);
        await context.Response.WriteAsync(UnavailableBody, context.RequestAborted);
    }
}
