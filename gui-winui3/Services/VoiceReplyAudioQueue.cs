using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RuApp.Services;

/// <summary>
/// Buffers generated reply audio independently from the single playback device.
/// </summary>
public sealed class VoiceReplyAudioQueue : IDisposable
{
    private readonly Func<VoiceAudioChunk, CancellationToken, Task> _playChunkAsync;
    private readonly Channel<VoiceAudioChunk> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _consumer;
    private int _disposed;
    private int _completionFinished;

    public VoiceReplyAudioQueue(
        Func<VoiceAudioChunk, CancellationToken, Task> playChunkAsync,
        int capacity = 4)
    {
        _playChunkAsync = playChunkAsync ?? throw new ArgumentNullException(nameof(playChunkAsync));
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _channel = Channel.CreateBounded<VoiceAudioChunk>(
            new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        _consumer = ConsumeAsync(_cancellation.Token);
    }

    public async Task EnqueueAsync(
        VoiceAudioChunk chunk,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        await _channel.Writer.WriteAsync(chunk, cancellationToken);
    }

    public async Task CompleteAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _consumer;
        }
        finally
        {
            Interlocked.Exchange(ref _completionFinished, 1);
            _cancellation.Dispose();
        }
    }

    public void Cancel()
    {
        _channel.Writer.TryComplete();
        try
        {
            _cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Completion may have disposed the cancellation source concurrently.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        if (Volatile.Read(ref _completionFinished) == 0)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Completion may have disposed the cancellation source concurrently.
            }
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (VoiceAudioChunk chunk in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                await _playChunkAsync(chunk, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the normal path when a newer reply interrupts playback.
        }
    }
}
