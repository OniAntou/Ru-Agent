namespace RuBackend.Compatibility;

public interface IUpstreamHttpTransport
{
    Task<UpstreamResponse> SendAsync(
        UpstreamRequest request,
        CancellationToken cancellationToken);
}
