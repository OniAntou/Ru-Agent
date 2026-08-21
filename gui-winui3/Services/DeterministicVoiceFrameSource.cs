using System.Diagnostics;

namespace RuApp.Services;

public sealed record DeterministicVoiceFrameOptions(
    TimeSpan SpeechDelay = default,
    TimeSpan EndDelay = default,
    bool EmitFault = false,
    TimeSpan LateFrameDelay = default)
{
    public void Validate()
    {
        ValidateDelay(SpeechDelay, nameof(SpeechDelay));
        ValidateDelay(EndDelay, nameof(EndDelay));
        ValidateDelay(LateFrameDelay, nameof(LateFrameDelay));
    }

    private static void ValidateDelay(TimeSpan delay, string name)
    {
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(name, "Deterministic voice delays must be between 0 and 5000 ms.");
        }
    }
}

/// <summary>
/// Emits deterministic 16 kHz mono PCM16 frames without opening an audio device.
/// A new instance represents one bounded voice turn.
/// </summary>
public sealed class DeterministicVoiceFrameSource : IVoiceFrameSource
{
    private const int FrameSamples = 320;
    private const int SpeechFrames = 12;
    private const int SilenceFrames = 48;
    private readonly DeterministicVoiceFrameOptions _options;
    private readonly object _gate = new();
    private CancellationTokenSource? _runCancellation;
    private Task? _runTask;
    private bool _disposed;

    public DeterministicVoiceFrameSource(DeterministicVoiceFrameOptions? options = null)
    {
        _options = options ?? new DeterministicVoiceFrameOptions();
        _options.Validate();
    }

    public event Action<ReadOnlyMemory<short>>? FrameReady;
    public event Action<Exception>? Faulted;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? previousCancellation = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_runTask is { IsCompleted: false })
            {
                return Task.CompletedTask;
            }

            previousCancellation = _runCancellation;
            _runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // Keep the synthetic producer off the WinUI dispatcher. The real
            // AudioGraph callback is not a UI callback either, and this avoids
            // a fast deterministic turn monopolising the desktop message loop.
            CancellationToken runToken = _runCancellation.Token;
            _runTask = Task.Run(
                () => EmitTurnAsync(runToken),
                CancellationToken.None);
        }

        previousCancellation?.Dispose();
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        Task? runTask;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            runTask = _runTask;
            cancellation = _runCancellation;
        }

        if (runTask == null)
        {
            return;
        }

        cancellation?.Cancel();
        try
        {
            await runTask.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_runTask, runTask))
                {
                    _runTask = null;
                    _runCancellation = null;
                }
            }

            cancellation?.Dispose();
        }
    }

    public void Dispose()
    {
        Task? runTask;
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            runTask = _runTask;
            cancellation = _runCancellation;
        }

        cancellation?.Cancel();
        try
        {
            runTask?.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DeterministicVoiceFrameSource] Dispose cleanup failed: {ex.Message}");
        }
        finally
        {
            cancellation?.Dispose();
            lock (_gate)
            {
                _runTask = null;
                _runCancellation = null;
            }
        }
    }

    private async Task EmitTurnAsync(CancellationToken cancellationToken)
    {
        try
        {
            await DelayAsync(_options.SpeechDelay, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (_options.EmitFault)
            {
                Faulted?.Invoke(new InvalidOperationException("Deterministic voice source fault."));
                return;
            }

            for (int frame = 0; frame < SpeechFrames; frame++)
            {
                Emit(BuildSpeechFrame(frame));
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }

            await DelayAsync(_options.EndDelay, cancellationToken).ConfigureAwait(false);
            for (int frame = 0; frame < SilenceFrames; frame++)
            {
                Emit(new short[FrameSamples]);
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_options.LateFrameDelay > TimeSpan.Zero)
            {
                await DelayAsync(_options.LateFrameDelay, cancellationToken).ConfigureAwait(false);
                Emit(BuildSpeechFrame(SpeechFrames));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
    }

    private void Emit(short[] samples)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        FrameReady?.Invoke(samples);
    }

    private static short[] BuildSpeechFrame(int frameNumber)
    {
        var samples = new short[FrameSamples];
        for (int index = 0; index < samples.Length; index++)
        {
            double phase = 2.0 * Math.PI * 220.0 * (frameNumber * FrameSamples + index) / 16_000.0;
            samples[index] = (short)Math.Round(8_000.0 * Math.Sin(phase));
        }

        return samples;
    }

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return delay == TimeSpan.Zero
            ? Task.CompletedTask
            : Task.Delay(delay, cancellationToken);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
