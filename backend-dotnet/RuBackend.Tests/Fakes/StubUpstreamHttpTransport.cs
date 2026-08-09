using RuBackend.Compatibility;

namespace RuBackend.Tests.Fakes;

public sealed class StubUpstreamHttpTransport : IUpstreamHttpTransport
{
    private readonly UpstreamResponse? _response;
    private readonly Exception? _error;

    public StubUpstreamHttpTransport(UpstreamResponse response)
    {
        _response = response;
    }

    public StubUpstreamHttpTransport(Exception error)
    {
        _error = error;
    }

    public Task<UpstreamResponse> SendAsync(
        UpstreamRequest request,
        CancellationToken cancellationToken)
    {
        if (_error is not null)
        {
            return Task.FromException<UpstreamResponse>(_error);
        }

        return Task.FromResult(_response!);
    }
}
