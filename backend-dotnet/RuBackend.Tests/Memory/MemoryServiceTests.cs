using System.Diagnostics;
using RuBackend.Memory;
using Xunit;

namespace RuBackend.Tests.Memory;

public sealed class MemoryServiceTests
{
    [Fact]
    public void Learning_queue_capacity_is_exactly_fifty()
    {
        Assert.Equal(50, MemoryLearningQueue.Capacity);
    }

    [Fact]
    public async Task Clear_waits_for_active_write_and_discards_queued_stale_turns()
    {
        var repository = new FakeMemoryRepository();
        var firstExtractionStarted = NewSignal();
        var releaseFirstExtraction = NewSignal();
        var extractionCount = 0;
        var extractor = new MemoryCandidateExtractor(async (prompt, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref extractionCount) == 1)
            {
                firstExtractionStarted.SetResult();
                await releaseFirstExtraction.Task.WaitAsync(cancellationToken);
            }

            return [Candidate(prompt)];
        });

        await using var service = CreateService(repository, extractor: extractor);
        service.EnqueueLearning("first", "response");
        await firstExtractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.EnqueueLearning("stale", "response");

        var clearTask = service.ClearAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(clearTask.IsCompleted);

        releaseFirstExtraction.SetResult();
        await clearTask;

        Assert.Single(repository.Upserted);
        Assert.Equal("first", repository.Upserted[0].Content);
        Assert.Equal(1, repository.ClearCount);
    }

    [Fact]
    public async Task A_second_writer_waits_for_the_first_writer()
    {
        var repository = new FakeMemoryRepository();
        var firstWriteStarted = NewSignal();
        var releaseFirstWrite = NewSignal();
        var writeCount = 0;
        repository.UpsertHook = async (candidate, cancellationToken) =>
        {
            if (Interlocked.Increment(ref writeCount) == 1)
            {
                firstWriteStarted.SetResult();
                await releaseFirstWrite.Task.WaitAsync(cancellationToken);
            }
        };
        var policy = new FakeMemoryPolicy
        {
            ExtractHandler = (prompt, _) => [Candidate(prompt)],
        };

        await using var service = CreateService(repository, policy);
        var first = service.RememberTurnAsync("first", "response", CancellationToken.None);
        await firstWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = service.RememberTurnAsync("second", "response", CancellationToken.None);

        try
        {
            await Task.Delay(50);
            Assert.Empty(repository.Upserted);
        }
        finally
        {
            releaseFirstWrite.SetResult();
        }

        await Task.WhenAll(first, second);

        Assert.Equal(2, repository.Upserted.Count);
    }

    [Fact]
    public async Task Extraction_failure_does_not_kill_the_learning_loop()
    {
        var repository = new FakeMemoryRepository();
        var secondWrite = NewSignal();
        var callCount = 0;
        var extractor = new MemoryCandidateExtractor((prompt, _, _) =>
        {
            if (Interlocked.Increment(ref callCount) == 1)
            {
                throw new InvalidOperationException("synthetic extraction failure");
            }

            secondWrite.SetResult();
            return Task.FromResult<IReadOnlyList<MemoryCandidate>>([Candidate(prompt)]);
        });

        await using var service = CreateService(repository, extractor: extractor);
        service.EnqueueLearning("failed", "response");
        service.EnqueueLearning("survived", "response");

        await secondWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => repository.Upserted.Count == 1);

        Assert.Equal("survived", repository.Upserted[0].Content);
    }

    [Fact]
    public async Task Dispose_waits_for_an_active_write_before_disposing_repository()
    {
        var repository = new FakeMemoryRepository();
        var writeStarted = NewSignal();
        var releaseWrite = NewSignal();
        repository.UpsertHook = async (_, cancellationToken) =>
        {
            writeStarted.SetResult();
            await releaseWrite.Task.WaitAsync(cancellationToken);
        };
        var policy = new FakeMemoryPolicy
        {
            ExtractHandler = (prompt, _) => [Candidate(prompt)],
        };
        var service = CreateService(repository, policy);

        var writeTask = service.RememberTurnAsync("active", "response", CancellationToken.None);
        await writeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var disposeTask = service.DisposeAsync().AsTask();

        await Task.Delay(50);
        Assert.False(repository.IsDisposed);

        releaseWrite.SetResult();
        await writeTask;
        await disposeTask;

        Assert.True(repository.IsDisposed);
    }

    private static MemoryService CreateService(
        FakeMemoryRepository repository,
        FakeMemoryPolicy? policy = null,
        MemorySqliteOptions? options = null,
        MemoryCandidateExtractor? extractor = null) =>
        new(
            repository,
            policy ?? new FakeMemoryPolicy { ExtractHandler = (prompt, _) => [Candidate(prompt)] },
            options ?? new MemorySqliteOptions(),
            extractor);

    private static MemoryCandidate Candidate(string content) =>
        new(MemoryKind.Preference, content, null, "synthetic evidence", 1, 0.8);

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ru-memory-task5", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(2))
            {
                throw new TimeoutException("Condition was not met within the test timeout.");
            }

            await Task.Delay(10);
        }
    }

    private sealed class FakeMemoryPolicy : IMemoryPolicy
    {
        public Func<string, string, IReadOnlyList<MemoryCandidate>> ExtractHandler { get; init; } =
            (_, _) => [];

        public IReadOnlyList<MemoryCandidate> ExtractCandidates(string prompt, string response) =>
            ExtractHandler(prompt, response);

        public bool IsSensitiveOrUnsafe(string text) => false;

        public string StripInternalMetadata(string text) => text;
    }

    private sealed class FakeMemoryRepository : IMemoryRepository
    {
        private readonly object _sync = new();

        public List<MemoryCandidate> Upserted { get; } = [];
        public int ClearCount { get; private set; }
        public bool IsDisposed { get; private set; }
        public Func<MemoryCandidate, CancellationToken, Task>? UpsertHook { get; set; }

        public Task<MemoryRecord?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult<MemoryRecord?>(null);

        public Task<IReadOnlyList<MemoryRecord>> ListActiveAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemoryRecord>>([]);

        public Task<IReadOnlyList<MemoryRecord>> RetrieveAsync(string query, int limit, int maxChars, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MemoryRecord>>([]);

        public async Task<MemoryRecord> UpsertAsync(MemoryCandidate candidate, CancellationToken cancellationToken)
        {
            if (UpsertHook is not null)
            {
                await UpsertHook(candidate, cancellationToken);
            }

            lock (_sync)
            {
                Upserted.Add(candidate);
            }

            var now = DateTimeOffset.UtcNow;
            return new MemoryRecord(
                Guid.NewGuid().ToString("D"),
                candidate.Kind,
                candidate.Content,
                candidate.Scope,
                candidate.Evidence,
                candidate.Confidence,
                candidate.Importance,
                now,
                now,
                null,
                candidate.ExpiresAt,
                "active",
                false);
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            ClearCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
