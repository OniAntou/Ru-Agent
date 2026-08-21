using System;

namespace RuApp.Services;

public enum VoiceActivityState
{
    WaitingForSpeech,
    Speaking,
    Completed,
}

public sealed record VoiceActivityOptions(
    int SampleRate,
    int FrameSamples,
    TimeSpan SpeechStart,
    TimeSpan TrailingSilence,
    TimeSpan MaxTurn,
    double MinimumRms,
    double NoiseMultiplier)
{
    public static VoiceActivityOptions Default { get; } = new(
        16_000,
        320,
        TimeSpan.FromMilliseconds(120),
        TimeSpan.FromMilliseconds(850),
        TimeSpan.FromSeconds(60),
        0.018,
        3.0);
}

public readonly record struct VoiceActivityFrameResult(
    VoiceActivityState State,
    bool IsSpeech,
    bool ShouldComplete,
    bool HasSpeech);

public sealed class VoiceActivityDetector
{
    private readonly VoiceActivityOptions _options;
    private readonly int _speechStartFrames;
    private readonly int _trailingSilenceFrames;
    private readonly int _maximumFrames;
    private readonly double _initialNoiseFloor;
    private int _frameCount;
    private int _voicedFrameCount;
    private int _silentFrameCount;
    private double _noiseFloor;
    private bool _hasSpeech;
    private VoiceActivityState _state;

    public VoiceActivityDetector(VoiceActivityOptions options)
    {
        if (options.SampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sample rate must be positive.");
        }
        if (options.FrameSamples <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Frame size must be positive.");
        }
        if (options.MinimumRms <= 0 || options.NoiseMultiplier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "RMS thresholds must be positive.");
        }

        _options = options;
        double frameMilliseconds = options.FrameSamples * 1000.0 / options.SampleRate;
        _speechStartFrames = Math.Max(1, (int)Math.Ceiling(options.SpeechStart.TotalMilliseconds / frameMilliseconds));
        _trailingSilenceFrames = Math.Max(1, (int)Math.Ceiling(options.TrailingSilence.TotalMilliseconds / frameMilliseconds));
        _maximumFrames = Math.Max(1, (int)Math.Ceiling(options.MaxTurn.TotalSeconds * options.SampleRate / options.FrameSamples));
        _initialNoiseFloor = options.MinimumRms / options.NoiseMultiplier;
        Reset();
    }

    public bool IsAtMaximumDuration => _frameCount >= _maximumFrames;

    public VoiceActivityFrameResult ProcessFrame(ReadOnlySpan<short> samples)
    {
        if (samples.Length != _options.FrameSamples)
        {
            throw new ArgumentException(
                $"Expected {_options.FrameSamples} samples, received {samples.Length}.",
                nameof(samples));
        }
        if (_state == VoiceActivityState.Completed)
        {
            throw new InvalidOperationException("The detector has already completed this turn.");
        }

        _frameCount++;
        double rms = CalculateRms(samples);
        double threshold = Math.Max(_options.MinimumRms, _noiseFloor * _options.NoiseMultiplier);
        bool isSpeech = rms >= threshold;

        if (!_hasSpeech)
        {
            if (isSpeech)
            {
                _voicedFrameCount++;
            }
            else
            {
                _voicedFrameCount = 0;
                _noiseFloor = (_noiseFloor * 0.95) + (rms * 0.05);
            }

            if (_voicedFrameCount >= _speechStartFrames)
            {
                _hasSpeech = true;
                _state = VoiceActivityState.Speaking;
                _silentFrameCount = 0;
            }
        }
        else if (isSpeech)
        {
            _silentFrameCount = 0;
        }
        else
        {
            _silentFrameCount++;
        }

        bool shouldComplete = _hasSpeech
            ? _silentFrameCount >= _trailingSilenceFrames
            : false;
        if (_frameCount >= _maximumFrames)
        {
            shouldComplete = true;
        }

        if (shouldComplete)
        {
            _state = VoiceActivityState.Completed;
        }

        return new VoiceActivityFrameResult(_state, isSpeech, shouldComplete, _hasSpeech);
    }

    public void Reset()
    {
        _frameCount = 0;
        _voicedFrameCount = 0;
        _silentFrameCount = 0;
        _noiseFloor = _initialNoiseFloor;
        _hasSpeech = false;
        _state = VoiceActivityState.WaitingForSpeech;
    }

    private static double CalculateRms(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (short sample in samples)
        {
            double normalized = sample / 32768.0;
            sum += normalized * normalized;
        }

        return Math.Sqrt(sum / samples.Length);
    }
}
