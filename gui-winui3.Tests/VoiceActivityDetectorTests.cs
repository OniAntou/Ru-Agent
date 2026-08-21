using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoiceActivityDetectorTests
{
    [Fact]
    public void SpeechStartsOnlyAfterTheConfiguredVoicedWindow()
    {
        var detector = new VoiceActivityDetector(VoiceActivityOptions.Default);

        for (int i = 0; i < 5; i++)
        {
            VoiceActivityFrameResult result = detector.ProcessFrame(Samples(320, 0.20));
            Assert.True(result.IsSpeech);
            Assert.Equal(VoiceActivityState.WaitingForSpeech, result.State);
            Assert.False(result.HasSpeech);
        }

        VoiceActivityFrameResult started = detector.ProcessFrame(Samples(320, 0.20));

        Assert.True(started.IsSpeech);
        Assert.Equal(VoiceActivityState.Speaking, started.State);
        Assert.True(started.HasSpeech);
        Assert.False(started.ShouldComplete);
    }

    [Fact]
    public void SilentFrameDoesNotStartSpeech()
    {
        var detector = new VoiceActivityDetector(VoiceActivityOptions.Default);

        VoiceActivityFrameResult result = detector.ProcessFrame(Samples(320, 0.0));

        Assert.False(result.IsSpeech);
        Assert.Equal(VoiceActivityState.WaitingForSpeech, result.State);
        Assert.False(result.HasSpeech);
        Assert.False(result.ShouldComplete);
    }

    [Fact]
    public void TrailingSilenceCompletesOnlyAfterSpeech()
    {
        var detector = new VoiceActivityDetector(VoiceActivityOptions.Default);
        for (int i = 0; i < 6; i++)
        {
            detector.ProcessFrame(Samples(320, 0.20));
        }

        for (int i = 0; i < 42; i++)
        {
            VoiceActivityFrameResult result = detector.ProcessFrame(Samples(320, 0.0));
            Assert.False(result.ShouldComplete);
            Assert.Equal(VoiceActivityState.Speaking, result.State);
        }

        VoiceActivityFrameResult completed = detector.ProcessFrame(Samples(320, 0.0));

        Assert.True(completed.ShouldComplete);
        Assert.Equal(VoiceActivityState.Completed, completed.State);
        Assert.True(completed.HasSpeech);
    }

    [Fact]
    public void MaximumTurnDurationCompletesBoundedCapture()
    {
        var detector = new VoiceActivityDetector(VoiceActivityOptions.Default);

        for (int i = 0; i < 2_999; i++)
        {
            VoiceActivityFrameResult result = detector.ProcessFrame(Samples(320, 0.20));
            Assert.False(result.ShouldComplete);
        }

        VoiceActivityFrameResult completed = detector.ProcessFrame(Samples(320, 0.20));

        Assert.True(completed.ShouldComplete);
        Assert.Equal(VoiceActivityState.Completed, completed.State);
        Assert.True(completed.HasSpeech);
    }

    [Fact]
    public void ResetStartsANewTurn()
    {
        var detector = new VoiceActivityDetector(VoiceActivityOptions.Default);
        for (int i = 0; i < 6; i++)
        {
            detector.ProcessFrame(Samples(320, 0.20));
        }

        detector.Reset();
        VoiceActivityFrameResult result = detector.ProcessFrame(Samples(320, 0.0));

        Assert.Equal(VoiceActivityState.WaitingForSpeech, result.State);
        Assert.False(result.HasSpeech);
        Assert.False(result.ShouldComplete);
    }

    [Fact]
    public void RmsClassificationIsSymmetricForPositiveAndNegativeSamples()
    {
        var positive = new VoiceActivityDetector(VoiceActivityOptions.Default);
        var negative = new VoiceActivityDetector(VoiceActivityOptions.Default);

        VoiceActivityFrameResult positiveResult = positive.ProcessFrame(Samples(320, 0.20));
        VoiceActivityFrameResult negativeResult = negative.ProcessFrame(Samples(320, -0.20));

        Assert.True(positiveResult.IsSpeech);
        Assert.True(negativeResult.IsSpeech);
        Assert.Equal(positiveResult.IsSpeech, negativeResult.IsSpeech);
    }

    [Fact]
    public void RejectsFramesWithUnexpectedSampleCount()
    {
        var detector = new VoiceActivityDetector(VoiceActivityOptions.Default);

        Assert.Throws<ArgumentException>(() => detector.ProcessFrame(new short[319]));
    }

    private static short[] Samples(int count, double amplitude)
    {
        short value = (short)Math.Round(Math.Clamp(amplitude, -1.0, 1.0) * short.MaxValue);
        return Enumerable.Repeat(value, count).ToArray();
    }
}
