using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Storage.Streams;

namespace RuApp.Services;

public enum VoiceTurnStopReason
{
    SpeechEnded,
    MaximumDuration,
    NoSpeech,
    Cancelled,
    CaptureError,
}

public sealed record VoiceTurnCaptureResult(
    byte[]? WavBytes,
    VoiceTurnStopReason Reason,
    Exception? Error = null);

public interface IVoiceFrameSource : IDisposable
{
    event Action<ReadOnlyMemory<short>>? FrameReady;
    event Action<Exception>? Faulted;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

public interface IVoiceTurnCapture : IDisposable
{
    bool IsCapturing { get; }

    Task<VoiceTurnCaptureResult> CaptureTurnAsync(CancellationToken cancellationToken);
    Task StopAsync();
}

/// <summary>
/// Captures one bounded speech turn in memory. It owns the microphone source for
/// the duration of the turn and never writes raw audio or transcripts to disk.
/// </summary>
public sealed class VoiceTurnCaptureService : IVoiceTurnCapture
{
    private const double PreRollMilliseconds = 250;

    private readonly Func<IVoiceFrameSource> _sourceFactory;
    private readonly VoiceActivityOptions _options;
    private readonly object _gate = new();
    private CaptureSession? _activeSession;
    private bool _disposed;

    public VoiceTurnCaptureService(
        Func<IVoiceFrameSource>? sourceFactory = null,
        VoiceActivityOptions? options = null)
    {
        _sourceFactory = sourceFactory ?? (() => new WindowsAudioGraphFrameSource());
        _options = options ?? VoiceActivityOptions.Default;
    }

    public bool IsCapturing
    {
        get
        {
            lock (_gate)
            {
                return _activeSession != null;
            }
        }
    }

    public async Task<VoiceTurnCaptureResult> CaptureTurnAsync(CancellationToken cancellationToken)
    {
        CaptureSession session;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_activeSession != null)
            {
                throw new InvalidOperationException("A voice turn is already being captured.");
            }

            session = new CaptureSession(_options);
            _activeSession = session;
        }

        try
        {
            _ = RunCaptureAsync(session, cancellationToken);
            return await session.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeSession, session))
                {
                    _activeSession = null;
                }
            }
        }
    }

    public async Task StopAsync()
    {
        CaptureSession? session;
        lock (_gate)
        {
            session = _activeSession;
        }

        if (session == null)
        {
            return;
        }

        session.StopSource.Cancel();
        await session.Completion.Task.ConfigureAwait(false);
    }

    public void Dispose()
    {
        CaptureSession? session;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            session = _activeSession;
        }

        if (session != null)
        {
            try
            {
                session.StopSource.Cancel();
                session.Completion.Task.GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoiceTurnCapture] Dispose cleanup failed: {ex.Message}");
            }
        }
    }

    private async Task RunCaptureAsync(CaptureSession session, CancellationToken callerCancellation)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            callerCancellation,
            session.StopSource.Token);
        IVoiceFrameSource? source = null;
        VoiceTurnCaptureResult? result = null;

        try
        {
            source = _sourceFactory();
            session.Source = source;
            source.FrameReady += session.HandleFrame;
            source.Faulted += session.HandleFault;

            await source.StartAsync(linkedCancellation.Token).ConfigureAwait(false);
            result = await session.TurnEnded.Task.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            result = new VoiceTurnCaptureResult(null, VoiceTurnStopReason.Cancelled);
        }
        catch (Exception ex)
        {
            result = new VoiceTurnCaptureResult(null, VoiceTurnStopReason.CaptureError, ex);
        }
        finally
        {
            if (source != null)
            {
                source.FrameReady -= session.HandleFrame;
                source.Faulted -= session.HandleFault;
                try
                {
                    await source.StopAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VoiceTurnCapture] Source stop failed: {ex.Message}");
                    if (result == null || result.Reason == VoiceTurnStopReason.SpeechEnded)
                    {
                        result = new VoiceTurnCaptureResult(null, VoiceTurnStopReason.CaptureError, ex);
                    }
                }

                try
                {
                    source.Dispose();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VoiceTurnCapture] Source dispose failed: {ex.Message}");
                    if (result == null)
                    {
                        result = new VoiceTurnCaptureResult(null, VoiceTurnStopReason.CaptureError, ex);
                    }
                }
            }

            try
            {
                session.StopSource.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoiceTurnCapture] Cancellation cleanup failed: {ex.Message}");
            }
            session.Completion.TrySetResult(result ?? new VoiceTurnCaptureResult(
                null,
                VoiceTurnStopReason.CaptureError,
                new InvalidOperationException("Voice capture ended without a result.")));
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(VoiceTurnCaptureService));
        }
    }

    private sealed class CaptureSession
    {
        private readonly VoiceActivityDetector _detector;
        private readonly Queue<short[]> _preRoll = new();
        private readonly List<short> _samples = new();
        private readonly object _frameGate = new();
        private readonly int _preRollFrames;
        private bool _hasSpeech;

        public CaptureSession(VoiceActivityOptions options)
        {
            _options = options;
            _detector = new VoiceActivityDetector(options);
            double frameMilliseconds = options.FrameSamples * 1000.0 / options.SampleRate;
            _preRollFrames = Math.Max(1, (int)Math.Ceiling(PreRollMilliseconds / frameMilliseconds));
        }

        public CancellationTokenSource StopSource { get; } = new();
        public TaskCompletionSource<VoiceTurnCaptureResult> TurnEnded { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<VoiceTurnCaptureResult> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public IVoiceFrameSource? Source { get; set; }

        public void HandleFrame(ReadOnlyMemory<short> frame)
        {
            VoiceTurnCaptureResult? result = null;
            lock (_frameGate)
            {
                if (TurnEnded.Task.IsCompleted)
                {
                    return;
                }

                short[] frameCopy = frame.ToArray();
                try
                {
                    bool hadSpeech = _hasSpeech;
                    VoiceActivityFrameResult frameResult = _detector.ProcessFrame(frameCopy);

                    if (!hadSpeech)
                    {
                        _preRoll.Enqueue(frameCopy);
                        while (_preRoll.Count > _preRollFrames)
                        {
                            _preRoll.Dequeue();
                        }
                    }

                    if (!_hasSpeech && frameResult.HasSpeech)
                    {
                        _hasSpeech = true;
                        foreach (short[] preRollFrame in _preRoll)
                        {
                            _samples.AddRange(preRollFrame);
                        }
                    }

                    if (_hasSpeech && hadSpeech)
                    {
                        _samples.AddRange(frameCopy);
                    }

                    if (frameResult.ShouldComplete)
                    {
                        VoiceTurnStopReason reason = frameResult.HasSpeech
                            ? (_detector.IsAtMaximumDuration
                                ? VoiceTurnStopReason.MaximumDuration
                                : VoiceTurnStopReason.SpeechEnded)
                            : VoiceTurnStopReason.NoSpeech;
                        byte[]? wav = reason is VoiceTurnStopReason.SpeechEnded or VoiceTurnStopReason.MaximumDuration
                            ? PcmWavEncoder.Encode(_samples, _options.SampleRate, 1)
                            : null;
                        result = new VoiceTurnCaptureResult(wav, reason);
                    }
                }
                catch (Exception ex)
                {
                    result = new VoiceTurnCaptureResult(null, VoiceTurnStopReason.CaptureError, ex);
                }
            }

            if (result != null)
            {
                TurnEnded.TrySetResult(result);
            }
        }

        public void HandleFault(Exception exception)
        {
            TurnEnded.TrySetResult(new VoiceTurnCaptureResult(
                null,
                VoiceTurnStopReason.CaptureError,
                exception));
        }

        private readonly VoiceActivityOptions _options;
    }
}

/// <summary>
/// Windows AudioGraph source that emits fixed-size mono PCM16 frames.
/// AudioGraph's QuantumStarted callback is synchronous, so the frame is copied
/// immediately and only the small PCM frame is sent to the capture service.
/// </summary>
public sealed class WindowsAudioGraphFrameSource : IVoiceFrameSource
{
    private const int SampleRate = 16_000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const int SamplesPerQuantum = 320;

    private readonly object _gate = new();
    private AudioGraph? _audioGraph;
    private AudioDeviceInputNode? _inputNode;
    private AudioFrameOutputNode? _outputNode;
    private bool _started;
    private bool _disposed;

    public event Action<ReadOnlyMemory<short>>? FrameReady;
    public event Action<Exception>? Faulted;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await StopAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        AudioGraph? graph = null;
        AudioDeviceInputNode? input = null;
        AudioFrameOutputNode? output = null;
        bool installed = false;
        try
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                EncodingProperties = AudioEncodingProperties.CreatePcm(
                    (uint)SampleRate,
                    (uint)Channels,
                    (uint)BitsPerSample),
                DesiredSamplesPerQuantum = SamplesPerQuantum,
            };

            var graphResult = await AudioGraph.CreateAsync(settings);
            if (graphResult.Status != AudioGraphCreationStatus.Success || graphResult.Graph == null)
            {
                throw new InvalidOperationException($"AudioGraph creation failed: {graphResult.Status}.");
            }

            graph = graphResult.Graph;
            AudioEncodingProperties encoding = settings.EncodingProperties;
            output = graph.CreateFrameOutputNode(encoding);
            CreateAudioDeviceInputNodeResult inputResult = await graph.CreateDeviceInputNodeAsync(
                MediaCategory.Speech,
                encoding);
            if (inputResult.Status != AudioDeviceNodeCreationStatus.Success || inputResult.DeviceInputNode == null)
            {
                throw new InvalidOperationException($"Microphone input creation failed: {inputResult.Status}.");
            }

            input = inputResult.DeviceInputNode;
            input.AddOutgoingConnection(output);
            graph.QuantumStarted += OnQuantumStarted;
            graph.UnrecoverableErrorOccurred += OnUnrecoverableError;

            lock (_gate)
            {
                ThrowIfDisposed();
                _audioGraph = graph;
                _inputNode = input;
                _outputNode = output;
                _started = true;
                installed = true;
                output.Start();
                graph.Start();
            }

            graph = null;
            input = null;
            output = null;
        }
        catch
        {
            if (installed)
            {
                await StopAsync().ConfigureAwait(false);
            }
            else if (graph != null)
            {
                graph.QuantumStarted -= OnQuantumStarted;
                graph.UnrecoverableErrorOccurred -= OnUnrecoverableError;
                input?.Dispose();
                output?.Dispose();
                graph.Dispose();
            }

            throw;
        }
    }

    public Task StopAsync()
    {
        AudioGraph? graph;
        AudioDeviceInputNode? input;
        AudioFrameOutputNode? output;
        lock (_gate)
        {
            graph = _audioGraph;
            input = _inputNode;
            output = _outputNode;
            _audioGraph = null;
            _inputNode = null;
            _outputNode = null;
            _started = false;
        }

        if (graph != null)
        {
            graph.QuantumStarted -= OnQuantumStarted;
            graph.UnrecoverableErrorOccurred -= OnUnrecoverableError;
            try
            {
                output?.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoiceCapture] Output stop failed: {ex.Message}");
            }

            try
            {
                graph.Stop();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VoiceCapture] Graph stop failed: {ex.Message}");
            }

            input?.Dispose();
            output?.Dispose();
            graph.Dispose();
        }

        return Task.CompletedTask;
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
        }

        StopAsync().GetAwaiter().GetResult();
    }

    private unsafe void OnQuantumStarted(AudioGraph sender, object args)
    {
        AudioFrameOutputNode? output;
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            output = _outputNode;
        }

        if (output == null)
        {
            return;
        }

        try
        {
            using AudioFrame frame = output.GetFrame();
            using AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
            using IMemoryBufferReference reference = buffer.CreateReference();
            var byteAccess = (IMemoryBufferByteAccess)reference;
            byteAccess.GetBuffer(out byte* data, out uint capacity);
            if (data == null || capacity < sizeof(short))
            {
                return;
            }

            int byteCount = checked((int)capacity);
            var bytes = new ReadOnlySpan<byte>(data, byteCount);
            short[] samples = DecodePcm16(bytes);
            if (samples.Length > 0)
            {
                FrameReady?.Invoke(samples);
            }
        }
        catch (Exception ex)
        {
            Faulted?.Invoke(ex);
        }
    }

    private void OnUnrecoverableError(AudioGraph sender, AudioGraphUnrecoverableErrorOccurredEventArgs args)
    {
        Faulted?.Invoke(new InvalidOperationException("AudioGraph reported an unrecoverable error."));
    }

    private static short[] DecodePcm16(ReadOnlySpan<byte> bytes)
    {
        int sampleCount = bytes.Length / sizeof(short);
        short[] samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            samples[i] = (short)(bytes[(i * 2)] | (bytes[(i * 2) + 1] << 8));
        }

        return samples;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsAudioGraphFrameSource));
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
