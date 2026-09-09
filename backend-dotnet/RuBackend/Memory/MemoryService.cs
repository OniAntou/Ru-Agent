using Microsoft.Extensions.Logging;

namespace RuBackend.Memory;

public delegate Task<IReadOnlyList<MemoryCandidate>> MemoryCandidateExtractor(
    string prompt,
    string response,
    CancellationToken cancellationToken);

public interface IMemoryService
{
    // Kept as a default for compatibility with temporary adapters created
    // before the list endpoint was introduced. MemoryService overrides it to
    // use the repository's active-record ordering and expiry semantics.
    Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken) =>
        RetrieveAsync(string.Empty, cancellationToken);

    Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(int limit, string? cursor, CancellationToken cancellationToken);
    Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> RememberTurnAsync(string prompt, string response, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
    void EnqueueLearning(string prompt, string response);
}

public sealed class MemoryService : IMemoryService, IAsyncDisposable
{
    private readonly IMemoryRepository _repository;
    private readonly IMemoryPolicy _policy;
    private readonly MemorySqliteOptions _options;
    private readonly MemoryCandidateExtractor _extractor;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _generationSync = new();
    private readonly object _disposeSync = new();
    private readonly MemoryLearningQueue _learningQueue;
    private Task? _disposeTask;
    private long _generation;
    private int _disposed;

    public MemoryService(
        IMemoryRepository repository,
        IMemoryPolicy policy,
        MemorySqliteOptions options,
        MemoryCandidateExtractor? extractor = null,
        ILogger<MemoryLearningQueue>? queueLogger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _extractor = extractor ?? ((prompt, response, _) =>
            Task.FromResult(_policy.ExtractCandidates(prompt, response)));
        _learningQueue = new MemoryLearningQueue(ProcessQueuedWorkAsync, queueLogger);
    }

public async Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        return await _repository.ListActiveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(int limit, string? cursor, CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        return await _repository.ListActiveAsync(limit, cursor, cancellationToken).ConfigureAwait(false);
    }

    public async Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        return await _repository.GetAsync(id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(
        string query,
        CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        return await _repository.RetrieveAsync(
            query,
            limit: 5,
            maxChars: _options.DefaultContextMaxChars,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryRecord>> RememberTurnAsync(
        string prompt,
        string response,
        CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = await ExtractCandidatesAsync(prompt, response, cancellationToken)
                .ConfigureAwait(false);
            return await UpsertCandidatesAsync(candidates, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void EnqueueLearning(string prompt, string response)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        long generation;
        lock (_generationSync)
        {
            generation = _generation;
        }

        _learningQueue.Enqueue(new MemoryWorkItem(generation, prompt, response));
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_generationSync)
            {
                _generation++;
            }

            _learningQueue.Drain();
            await _repository.ClearAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task ProcessQueuedWorkAsync(MemoryWorkItem item, CancellationToken cancellationToken)
    {
        if (!IsCurrentGeneration(item.Generation))
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrentGeneration(item.Generation))
            {
                return;
            }

            var candidates = await ExtractCandidatesAsync(item.Prompt, item.Response, cancellationToken)
                .ConfigureAwait(false);
            await UpsertCandidatesAsync(candidates, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<IReadOnlyList<MemoryRecord>> UpsertCandidatesAsync(
        IReadOnlyList<MemoryCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var records = new List<MemoryRecord>(candidates.Count);
        foreach (var candidate in candidates)
        {
            records.Add(await _repository.UpsertAsync(candidate, cancellationToken).ConfigureAwait(false));
        }

        return records;
    }

    private Task<IReadOnlyList<MemoryCandidate>> ExtractCandidatesAsync(
        string prompt,
        string response,
        CancellationToken cancellationToken) =>
        _extractor(prompt, response, cancellationToken);

    private bool IsCurrentGeneration(long generation)
    {
        lock (_generationSync)
        {
            return generation == _generation;
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _learningQueue.Drain();
        await _learningQueue.DisposeAsync().ConfigureAwait(false);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _repository.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
            _writeGate.Dispose();
        }
    }

    private void EnsureNotDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
