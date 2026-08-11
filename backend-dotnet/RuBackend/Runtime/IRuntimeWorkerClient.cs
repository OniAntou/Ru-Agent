using RuBackend.Compatibility;

namespace RuBackend.Runtime;

public sealed record RuntimeWorkerRequest(
    HttpMethod Method,
    string PathAndQuery,
    string? ContentType,
    byte[] Body);

public interface IRuntimeWorkerClient
{
    Task<UpstreamResponse> SendAsync(
        RuntimeWorkerRequest request,
        CancellationToken cancellationToken);
}
