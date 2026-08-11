namespace RuBackend.Conversation;

public sealed class ConversationTurnGate
{
    private static readonly TimeSpan DefaultMemoryTimeout = TimeSpan.FromSeconds(120);

    private readonly SemaphoreSlim _turn = new(1, 1);
    private readonly SemaphoreSlim _memory = new(1, 1);
    private readonly TimeSpan _memoryTimeout;

    public ConversationTurnGate(TimeSpan? memoryTimeout = null)
    {
        _memoryTimeout = memoryTimeout ?? DefaultMemoryTimeout;
        if (_memoryTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryTimeout));
        }
    }

    public IDisposable? TryEnterTurn() =>
        _turn.Wait(0) ? new Lease(_turn) : null;

    public async ValueTask<IAsyncDisposable?> EnterMemoryAsync(
        CancellationToken cancellationToken)
    {
        var acquired = await _memory.WaitAsync(_memoryTimeout, cancellationToken)
            .ConfigureAwait(false);
        return acquired ? new Lease(_memory) : null;
    }

    private sealed class Lease : IDisposable, IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;

        public Lease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
