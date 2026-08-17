using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace RuApp.Services;

public readonly record struct VoiceSubtitleCue(string Id, string Text);

public delegate Task VoiceReplyChunkPlayer(
    VoiceAudioChunk chunk,
    Action onPlaybackStarted,
    CancellationToken cancellationToken);

/// <summary>
/// Buffers generated reply audio independently from the single playback device.
/// </summary>
public sealed class VoiceReplyAudioQueue : IDisposable
{
    private readonly VoiceReplyChunkPlayer _playChunkAsync;
    private readonly VoiceSubtitleEventPump? _subtitleEvents;
    private readonly Channel<QueuedAudio> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _consumer;
    private int _disposed;
    private int _completionFinished;
    private int _writerClosed;

    public VoiceReplyAudioQueue(
        VoiceReplyChunkPlayer playChunkAsync,
        int capacity = 4,
        VoiceSubtitleEventPump? subtitleEvents = null)
    {
        _playChunkAsync = playChunkAsync ?? throw new ArgumentNullException(nameof(playChunkAsync));
        _subtitleEvents = subtitleEvents;
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _channel = Channel.CreateBounded<QueuedAudio>(
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
        await EnqueueAsync(chunk, null, cancellationToken);
    }

    public async Task EnqueueAsync(
        VoiceAudioChunk chunk,
        VoiceSubtitleCue? subtitleCue,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        try
        {
            await _channel.Writer.WriteAsync(new QueuedAudio(chunk, subtitleCue), cancellationToken);
        }
        catch (ChannelClosedException) when (Volatile.Read(ref _writerClosed) != 0)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    public async Task CompleteAsync()
    {
        Interlocked.Exchange(ref _writerClosed, 1);
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
        if (Interlocked.Exchange(ref _writerClosed, 1) != 0)
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
            return;
        }

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

        Interlocked.Exchange(ref _writerClosed, 1);
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
        string? activeSubtitleId = null;
        try
        {
            await foreach (QueuedAudio queued in
                _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                VoiceSubtitleCue? cue = queued.SubtitleCue;
                int notified = 0;
                await _playChunkAsync(
                    queued.Chunk,
                    () =>
                    {
                        if (Interlocked.Exchange(ref notified, 1) != 0 ||
                            cue is not VoiceSubtitleCue subtitle)
                        {
                            return;
                        }

                        if (_subtitleEvents?.TryShow(subtitle) == true)
                        {
                            activeSubtitleId = subtitle.Id;
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the normal path when a newer reply interrupts playback.
        }
        finally
        {
            if (activeSubtitleId is not null)
            {
                _subtitleEvents?.TryHide(activeSubtitleId);
            }
        }
    }

    private readonly record struct QueuedAudio(
        VoiceAudioChunk Chunk,
        VoiceSubtitleCue? SubtitleCue);
}
