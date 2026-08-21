using System.Diagnostics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;

namespace RuApp.Services;

public sealed class MediaPlayerVoicePlaybackDevice : IVoicePlaybackDevice
{
    private readonly object _gate = new();
    private MediaPlayer? _player;
    private InMemoryRandomAccessStream? _stream;
    private bool _disposed;

    public bool IsPlaying => _player?.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;

    public async Task PlayAsync(
        byte[] audio,
        CancellationToken cancellationToken,
        Action? onStarted = null)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var player = new MediaPlayer { AutoPlay = false };
        var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBytes(audio);
        await writer.StoreAsync();
        writer.DetachStream();
        stream.Seek(0);

        var opened = NewCompletionSource();
        var ended = NewCompletionSource();
        player.MediaOpened += (_, _) => opened.TrySetResult(true);
        player.MediaEnded += (_, _) => ended.TrySetResult(true);
        player.MediaFailed += (_, _) =>
        {
            var error = new InvalidOperationException("The audio chunk could not be played.");
            opened.TrySetException(error);
            ended.TrySetException(error);
        };

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _player = player;
            _stream = stream;
        }

        try
        {
            player.Source = MediaSource.CreateFromStream(stream, "audio/wav");
            await opened.Task.WaitAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            player.Play();
            try
            {
                onStarted?.Invoke();
            }
            catch (Exception error)
            {
                Debug.WriteLine($"[VoicePlaybackDevice] Playback-start observer failed: {error.Message}");
            }
            await ended.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            Cleanup(player, stream);
        }
    }

    public void Stop()
    {
        MediaPlayer? player;
        InMemoryRandomAccessStream? stream;
        lock (_gate)
        {
            player = _player;
            stream = _stream;
            _player = null;
            _stream = null;
        }

        Cleanup(player, stream);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    private void Cleanup(MediaPlayer? player, InMemoryRandomAccessStream? stream)
    {
        if (player is null)
        {
            stream?.Dispose();
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_player, player))
            {
                _player = null;
            }

            if (ReferenceEquals(_stream, stream))
            {
                _stream = null;
            }
        }
        try
        {
            player.Pause();
            player.Source = null;
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[VoicePlaybackDevice] Player cleanup failed: {error.Message}");
        }
        player.Dispose();
        stream?.Dispose();
    }

    private static TaskCompletionSource<bool> NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
