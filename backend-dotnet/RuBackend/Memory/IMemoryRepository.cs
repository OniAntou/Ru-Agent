namespace RuBackend.Memory;

public interface IMemoryRepository : IAsyncDisposable
{
    Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(string query, int limit, int maxChars, CancellationToken cancellationToken);
    Task<MemoryRecord> UpsertAsync(MemoryCandidate candidate, CancellationToken cancellationToken);
    Task<bool> IsLegacyMigrationRecordedAsync(string sourceHash, CancellationToken cancellationToken);
    Task RecordLegacyMigrationAsync(string sourceHash, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
}
