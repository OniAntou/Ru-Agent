using RuBackend.Compatibility;
using RuBackend.Worker;

namespace RuBackend.Runtime;

public sealed class LoopbackRuntimeWorkerClient : IRuntimeWorkerClient
{
    private readonly IRuntimeProtocolClient _protocol;

    public LoopbackRuntimeWorkerClient(IRuntimeProtocolClient protocol)
    {
        _protocol = protocol;
    }

    public async Task<UpstreamResponse> SendAsync(
        RuntimeWorkerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var response = await _protocol.InvokeRuntimeAsync(
                new WorkerRuntimeRequest(
                    $"runtime-{Guid.NewGuid():N}",
                    request.Method.Method.ToUpperInvariant(),
                    request.PathAndQuery,
                    request.ContentType,
                    request.Body),
                cancellationToken).ConfigureAwait(false);
            return new UpstreamResponse(
                response.StatusCode,
                response.ContentType ?? string.Empty,
                response.Body,
                response.Headers);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is WorkerProtocolException
            or WorkerUnavailableException)
        {
            throw new HttpRequestException("Private runtime worker request failed.", error);
        }
    }
}
