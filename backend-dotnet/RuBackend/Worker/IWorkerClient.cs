namespace RuBackend.Worker;

public interface IWorkerClient : IAsyncDisposable
{
    Task<WorkerHello> ConnectAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<WorkerEvent> GenerateChatAsync(
        WorkerChatRequest request,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkerFact>> ExtractFactsAsync(
        string requestId,
        string prompt,
        string response,
        CancellationToken cancellationToken);

    Task<VtsCatalog?> GetVtsCatalogAsync(
        string requestId,
        CancellationToken cancellationToken);

    Task TriggerVtsActionsAsync(
        string requestId,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken);

    Task CancelAsync(string requestId, CancellationToken cancellationToken);

    Task<WorkerHealth> GetHealthAsync(CancellationToken cancellationToken);
}
