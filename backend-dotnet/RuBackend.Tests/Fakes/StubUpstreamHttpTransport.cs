using RuBackend.Compatibility;

namespace RuBackend.Tests.Fakes;

public sealed class StubUpstreamHttpTransport : IUpstreamHttpTransport
{
    private readonly UpstreamResponse? _response;
    private readonly Exception? _error;
    private readonly Func<UpstreamRequest, UpstreamResponse>? _responseFactory;

    public UpstreamRequest? LastRequest { get; private set; }

    public StubUpstreamHttpTransport(UpstreamResponse response)
    {
        _response = response;
    }

    public StubUpstreamHttpTransport(Exception error)
    {
        _error = error;
    }

    public StubUpstreamHttpTransport(Func<UpstreamRequest, UpstreamResponse> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public Task<UpstreamResponse> SendAsync(
        UpstreamRequest request,
        CancellationToken cancellationToken)
    {
        LastRequest = request;
        if (_error is not null)
        {
            return Task.FromException<UpstreamResponse>(_error);
        }

        return Task.FromResult(_responseFactory is not null
            ? _responseFactory(request)
            : _response!);
    }
}
