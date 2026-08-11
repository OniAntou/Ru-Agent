using Microsoft.Extensions.Options;
using RuBackend.Compatibility;
using RuBackend.Configuration;

namespace RuBackend.Runtime;

public sealed class FastApiRuntimeWorkerClient : IRuntimeWorkerClient
{
    private readonly IUpstreamHttpTransport _transport;
    private readonly IOptions<BackendOptions> _options;

    public FastApiRuntimeWorkerClient(
        IUpstreamHttpTransport transport,
        IOptions<BackendOptions> options)
    {
        _transport = transport;
        _options = options;
    }

    public Task<UpstreamResponse> SendAsync(
        RuntimeWorkerRequest request,
        CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var token = _options.Value.DesktopToken?.Trim() ?? string.Empty;
        if (token.Length > 0)
        {
            headers["Authorization"] = $"Bearer {token}";
        }

        if (!string.IsNullOrWhiteSpace(request.ContentType))
        {
            headers["Content-Type"] = request.ContentType;
        }

        return _transport.SendAsync(
            new UpstreamRequest(
                request.Method,
                request.PathAndQuery,
                headers,
                request.Body),
            cancellationToken);
    }
}
