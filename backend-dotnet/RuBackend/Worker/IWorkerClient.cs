namespace RuBackend.Worker;

public interface IWorkerConnectionClient : IAsyncDisposable
{
    Task<WorkerHello> ConnectAsync(CancellationToken cancellationToken);

    Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken);
}
