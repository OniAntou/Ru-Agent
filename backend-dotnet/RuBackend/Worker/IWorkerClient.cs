namespace RuBackend.Worker;

public interface IWorkerConnectionClient : IAsyncDisposable
{
    Task<WorkerHello> ConnectAsync(CancellationToken cancellationToken);

    Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken);
}

public interface IVtsProtocolClient
{
    Task<VtsCatalog?> GetVtsCatalogAsync(
        string requestId,
        CancellationToken cancellationToken);

    Task TriggerVtsActionsAsync(
        string requestId,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken);
}
