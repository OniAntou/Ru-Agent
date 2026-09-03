using System.Security.Cryptography;
using System.Text.Json;
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

    Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(string query, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemoryRecord>> RememberTurnAsync(string prompt, string response, CancellationToken cancellationToken);
    Task ClearAsync(CancellationToken cancellationToken);
    void EnqueueLearning(string prompt, string response);
}

public sealed record MemoryLegacyImportResult(int Imported, int Rejected);

public sealed class MemoryService : IMemoryService, IAsyncDisposable
{
    private const long MaximumLegacyFileBytes = 16 * 1024 * 1024;
    private const string UserPrefix = "User: ";
    private const string AssistantMarker = "\nAssistant:";

    private readonly IMemoryRepository _repository;
    private readonly IMemoryPolicy _policy;
    private readonly MemorySqliteOptions _options;
    private readonly MemoryCandidateExtractor _extractor;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _generationSync = new();
    private readonly object _initializationSync = new();
    private readonly MemoryLearningQueue _learningQueue;
    private Task<MemoryLegacyImportResult>? _initializationTask;
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

    public Task<MemoryLegacyImportResult> InitializeAsync(CancellationToken cancellationToken) =>
        EnsureInitializedAsync(cancellationToken);

    public async Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _repository.ListActiveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(
        string query,
        CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_generationSync)
            {
                _generation++;
            }

            _learningQueue.Drain();
            await _repository.ClearAsync(cancellationToken).ConfigureAwait(false);
            lock (_initializationSync)
            {
                _initializationTask = null;
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_initializationSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task<MemoryLegacyImportResult> EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        EnsureNotDisposed();
        Task<MemoryLegacyImportResult> initializationTask;
        lock (_initializationSync)
        {
            _initializationTask ??= InitializeCoreAsync();
            initializationTask = _initializationTask;
        }

        try
        {
            return await initializationTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_initializationSync)
            {
                if (ReferenceEquals(_initializationTask, initializationTask))
                {
                    _initializationTask = null;
                }
            }

            throw;
        }
    }

    private async Task<MemoryLegacyImportResult> InitializeCoreAsync()
    {
        if (_options.ReadOnly)
        {
            return new MemoryLegacyImportResult(0, 0);
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = Path.GetFullPath(_options.LegacyTextsPath);
            if (!File.Exists(path))
            {
                return new MemoryLegacyImportResult(0, 0);
            }

            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaximumLegacyFileBytes)
            {
                return new MemoryLegacyImportResult(0, 1);
            }

            byte[] source;
            try
            {
                source = await ReadBoundedBytesAsync(path, MaximumLegacyFileBytes).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                return new MemoryLegacyImportResult(0, 1);
            }
            var sourceHash = Convert.ToHexString(SHA256.HashData(source)).ToLowerInvariant();
            if (await _repository.IsLegacyMigrationRecordedAsync(sourceHash, CancellationToken.None)
                    .ConfigureAwait(false))
            {
                return new MemoryLegacyImportResult(0, 0);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(source);
            }
            catch (JsonException)
            {
                return new MemoryLegacyImportResult(0, 1);
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return new MemoryLegacyImportResult(0, 1);
                }

                var imported = 0;
                var rejected = 0;
                foreach (var block in document.RootElement.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.String)
                    {
                        rejected++;
                        continue;
                    }

                    var text = block.GetString() ?? string.Empty;
                    if (!text.StartsWith(UserPrefix, StringComparison.Ordinal) ||
                        !text.Contains(AssistantMarker, StringComparison.Ordinal))
                    {
                        rejected++;
                        continue;
                    }

                    var transcript = text[UserPrefix.Length..];
                    var assistantIndex = transcript.IndexOf(AssistantMarker, StringComparison.Ordinal);
                    if (assistantIndex < 0)
                    {
                        rejected++;
                        continue;
                    }

                    var prompt = transcript[..assistantIndex];
                    var response = transcript[(assistantIndex + AssistantMarker.Length)..];
                    IReadOnlyList<MemoryCandidate> candidates;
                    try
                    {
                        candidates = await ExtractCandidatesAsync(prompt, response, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        rejected++;
                        continue;
                    }

                    if (candidates.Count == 0)
                    {
                        rejected++;
                        continue;
                    }

                    foreach (var candidate in candidates)
                    {
                        await _repository.UpsertAsync(candidate, CancellationToken.None).ConfigureAwait(false);
                        imported++;
                    }
                }

                await _repository.RecordLegacyMigrationAsync(sourceHash, CancellationToken.None)
                    .ConfigureAwait(false);
                return new MemoryLegacyImportResult(imported, rejected);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ProcessQueuedWorkAsync(MemoryWorkItem item, CancellationToken cancellationToken)
    {
        if (!IsCurrentGeneration(item.Generation))
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task<byte[]> ReadBoundedBytesAsync(string path, long maximumBytes)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new MemoryStream((int)Math.Min(maximumBytes, 81920));
        var buffer = new byte[(int)Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (output.Length > maximumBytes - read)
                throw new InvalidDataException("memory_legacy_file_too_large");
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }
        return output.ToArray();
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
