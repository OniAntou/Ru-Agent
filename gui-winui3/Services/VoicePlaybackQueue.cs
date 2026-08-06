using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RuApp.Services;

public sealed class VoicePlaybackQueue : IDisposable
{
    private readonly Func<VoiceAudioChunk, CancellationToken, Task> _playChunkAsync;
    private readonly object _gate = new();
    private Channel<VoiceAudioChunk>? _channel;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _consumer;
    private string? _activeRequestId;
    private int _nextSequence;
    private bool _disposed;

    public VoicePlaybackQueue(Func<VoiceAudioChunk, CancellationToken, Task> playChunkAsync)
    {
        _playChunkAsync = playChunkAsync ?? throw new ArgumentNullException(nameof(playChunkAsync));
    }

    public async Task StartAsync(string requestId)
    {
        ArgumentException.ThrowIfNullOrEmpty(requestId);
        ThrowIfDisposed();
        string? previousRequest;
        lock (_gate)
        {
            previousRequest = _activeRequestId;
        }
        if (previousRequest is not null)
        {
            Cancel(previousRequest);
        }

        var channel = Channel.CreateBounded<VoiceAudioChunk>(
            new BoundedChannelOptions(2) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        var cancellation = new CancellationTokenSource();
        lock (_gate)
        {
            _channel = channel;
            _sessionCancellation = cancellation;
            _activeRequestId = requestId;
            _nextSequence = 0;
            _consumer = ConsumeAsync(channel.Reader, cancellation.Token);
        }
        await Task.CompletedTask;
    }

    public async Task<bool> EnqueueAsync(
        string requestId,
        VoiceAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ChannelWriter<VoiceAudioChunk>? writer;
        lock (_gate)
        {
            if (_activeRequestId != requestId || chunk.RequestId != requestId || chunk.Sequence != _nextSequence)
            {
                return false;
            }
            _nextSequence++;
            writer = _channel?.Writer;
        }
        if (writer is null)
        {
            return false;
        }
        await writer.WriteAsync(chunk, cancellationToken);
        return true;
    }

    public async Task CompleteAsync(string requestId)
    {
        Task? consumer;
        lock (_gate)
        {
            if (_activeRequestId != requestId)
            {
                return;
            }
            _channel?.Writer.TryComplete();
            consumer = _consumer;
        }
        if (consumer is not null)
        {
            await consumer;
        }
        ClearSession(requestId);
    }

    public void Cancel(string requestId)
    {
        lock (_gate)
        {
            if (_activeRequestId != requestId)
            {
                return;
            }
            _activeRequestId = null;
            _sessionCancellation?.Cancel();
            _channel?.Writer.TryComplete();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _sessionCancellation?.Cancel();
            _channel?.Writer.TryComplete();
            _activeRequestId = null;
        }
    }

    private async Task ConsumeAsync(ChannelReader<VoiceAudioChunk> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (VoiceAudioChunk chunk in reader.ReadAllAsync(cancellationToken))
            {
                await _playChunkAsync(chunk, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the normal path when a newer reply interrupts playback.
        }
    }

    private void ClearSession(string requestId)
    {
        lock (_gate)
        {
            if (_activeRequestId == requestId)
            {
                _activeRequestId = null;
            }
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            _channel = null;
            _consumer = null;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
