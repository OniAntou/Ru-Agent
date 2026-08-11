namespace RuBackend.Worker;

public interface IRuntimeProtocolClient
{
    Task<WorkerRuntimeResponse> InvokeRuntimeAsync(
        WorkerRuntimeRequest request,
        CancellationToken cancellationToken);
}
