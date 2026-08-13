using Microsoft.AspNetCore.Http;
using RuBackend.Runtime;

namespace RuBackend.Tests.Fakes;

public sealed class StubRuntimeWorkerClient : IRuntimeWorkerClient
{
    private readonly Func<RuntimeWorkerRequest, RuntimeWorkerResponse>? _responseFactory;
    private readonly Exception? _error;

    public StubRuntimeWorkerClient(RuntimeWorkerResponse response)
        : this(_ => response)
    {
    }

    public StubRuntimeWorkerClient(Func<RuntimeWorkerRequest, RuntimeWorkerResponse> responseFactory)
    {
        _responseFactory = responseFactory;
    }

    public StubRuntimeWorkerClient(Exception error)
    {
        _error = error;
    }

    public List<RuntimeWorkerRequest> Requests { get; } = [];

    public Task<RuntimeWorkerResponse> SendAsync(
        RuntimeWorkerRequest request,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (_error is not null)
        {
            return Task.FromException<RuntimeWorkerResponse>(_error);
        }

        return Task.FromResult(_responseFactory!(request));
    }

    public static StubRuntimeWorkerClient Healthy() => new(new RuntimeWorkerResponse(
        StatusCodes.Status200OK,
        "application/json",
        "{\"status\":\"ok\",\"message\":\"Backend is running and ready for the WinUI3 UI.\"}"u8.ToArray()));
}
