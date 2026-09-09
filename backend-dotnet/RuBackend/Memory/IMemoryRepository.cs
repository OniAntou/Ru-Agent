namespace RuBackend.Memory;

public interface IMemoryRepository : IAsyncDisposable
{
    Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(int limit, string? cursor, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(string query, int limit, int maxChars, CancellationToken cancellationToken);
    Task<MemoryRecord> UpsertAsync(MemoryCandidate candidate, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}
