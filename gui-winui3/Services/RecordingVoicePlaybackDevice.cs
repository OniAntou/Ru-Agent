using System.Buffers.Binary;
using System.Text;

namespace RuApp.Services;

public sealed class RecordingVoicePlaybackDevice : IVoicePlaybackDevice
{
    private readonly HarnessEventSink _events;
    private readonly TimeSpan _playbackDelay;
    private readonly object _gate = new();
    private CancellationTokenSource? _stopCancellation;
    private bool _disposed;
    private bool _isPlaying;
    private int _playCalls;
    private int _completedCalls;
    private int _errorCalls;

    public RecordingVoicePlaybackDevice(
        HarnessEventSink events,
        TimeSpan? playbackDelay = null)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _playbackDelay = ValidateDelay(playbackDelay ?? TimeSpan.Zero);
    }

    public int PlayCalls => Volatile.Read(ref _playCalls);
    public int CompletedCalls => Volatile.Read(ref _completedCalls);
    public int ErrorCalls => Volatile.Read(ref _errorCalls);
    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _isPlaying;
            }
        }
    }

    public async Task PlayAsync(
        byte[] audio,
        CancellationToken cancellationToken,
        Action? onStarted = null)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ValidateWav(audio);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _stopCancellation?.Cancel();
            _stopCancellation = linked;
            _isPlaying = true;
            Interlocked.Increment(ref _playCalls);
        }

        try
        {
            _events.Write("voice.playback.started");
            onStarted?.Invoke();
            if (_playbackDelay > TimeSpan.Zero)
            {
                await Task.Delay(_playbackDelay, linked.Token).ConfigureAwait(false);
            }
            linked.Token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _completedCalls);
            _events.Write("voice.playback.ended");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            Interlocked.Increment(ref _errorCalls);
            _events.Write("voice.playback.ended", new { error = true });
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_stopCancellation, linked))
                {
                    _stopCancellation = null;
                    _isPlaying = false;
                }
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            _stopCancellation?.Cancel();
            _isPlaying = false;
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
            _stopCancellation?.Cancel();
            _isPlaying = false;
        }
    }

    private static TimeSpan ValidateDelay(TimeSpan value)
    {
        if (value < TimeSpan.Zero || value > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Playback delay must be between 0 and 5 seconds.");
        }

        return value;
    }

    private static void ValidateWav(byte[] audio)
    {
        if (audio.Length < 44
            || !audio.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            || !audio.AsSpan(8, 4).SequenceEqual("WAVE"u8)
            || !audio.AsSpan(12, 4).SequenceEqual("fmt "u8)
            || !audio.AsSpan(36, 4).SequenceEqual("data"u8))
        {
            throw new InvalidDataException("Playback requires a PCM WAV payload.");
        }

        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(audio.AsSpan(20, 2));
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(audio.AsSpan(22, 2));
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(24, 4));
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(audio.AsSpan(34, 2));
        uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(audio.AsSpan(40, 4));
        if (format != 1 || channels != 1 || sampleRate != 16_000 || bits != 16
            || dataSize == 0 || dataSize > audio.Length - 44)
        {
            throw new InvalidDataException("Playback WAV dimensions are invalid.");
        }
    }
}
