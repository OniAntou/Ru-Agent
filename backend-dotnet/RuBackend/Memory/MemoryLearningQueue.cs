using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RuBackend.Memory;

public sealed record MemoryWorkItem(long Generation, string Prompt, string Response);

public sealed class MemoryLearningQueue : IAsyncDisposable
{
    public const int Capacity = 50;

    private readonly Channel<MemoryWorkItem> _channel;
    private readonly Func<MemoryWorkItem, CancellationToken, Task> _processor;
    private readonly ILogger<MemoryLearningQueue> _logger;
    private readonly Task _worker;
    private int _disposed;

    public MemoryLearningQueue(
        Func<MemoryWorkItem, CancellationToken, Task> processor,
        ILogger<MemoryLearningQueue>? logger = null)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _logger = logger ?? NullLogger<MemoryLearningQueue>.Instance;
        _channel = Channel.CreateBounded<MemoryWorkItem>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = RunAsync();
    }

    public bool Enqueue(MemoryWorkItem item)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        return _channel.Writer.TryWrite(item);
    }

    public void Drain()
    {
        while (_channel.Reader.TryRead(out _))
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _channel.Writer.TryComplete();
        }

        await _worker.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        await foreach (var item in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await _processor(item, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Memory learning work failed with code {ErrorCode}.", "learning_cancelled");
            }
            catch (Exception)
            {
                // Keep diagnostics stable and content-free. In particular, do
                // not pass the exception because a provider may include a prompt.
                _logger.LogWarning("Memory learning work failed with code {ErrorCode}.", "learning_failed");
            }
        }
    }
}
