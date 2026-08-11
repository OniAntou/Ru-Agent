using RuBackend.Compatibility;
using RuBackend.Runtime;

namespace RuBackend.Tests.Fakes;

public sealed class StubRuntimeWorkerClient : IRuntimeWorkerClient
{
    private readonly Func<RuntimeWorkerRequest, UpstreamResponse>? _responseFactory;
    private readonly Exception? _error;

    public StubRuntimeWorkerClient(UpstreamResponse response)
        : this(_ => response)
    {
    }

    public StubRuntimeWorkerClient(Func<RuntimeWorkerRequest, UpstreamResponse> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public StubRuntimeWorkerClient(Exception error)
    {
        _error = error;
    }

    public List<RuntimeWorkerRequest> Requests { get; } = [];

    public Task<UpstreamResponse> SendAsync(
        RuntimeWorkerRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_error is not null)
        {
            return Task.FromException<UpstreamResponse>(_error);
        }

        return Task.FromResult(_responseFactory!(request));
    }
}
