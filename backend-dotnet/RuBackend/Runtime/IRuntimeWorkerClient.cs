namespace RuBackend.Runtime;

public sealed record RuntimeWorkerRequest(
    HttpMethod Method,
    string PathAndQuery,
    string? ContentType,
    byte[] Body);

public interface IRuntimeWorkerClient
{
    Task<RuntimeWorkerResponse> SendAsync(
        RuntimeWorkerRequest request,
        CancellationToken cancellationToken);
}
