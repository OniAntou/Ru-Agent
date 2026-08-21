using System.Collections.Concurrent;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoiceTurnCaptureServiceTests
{
    [Fact]
    public async Task CapturesAVoicedTurnAndReleasesTheSource()
    {
        var source = new FakeVoiceFrameSource();
        var service = new VoiceTurnCaptureService(() => source);

        Task<VoiceTurnCaptureResult> capture = service.CaptureTurnAsync(CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        for (int i = 0; i < 6; i++)
        {
            source.Emit(Samples(320, 0.20));
        }
        for (int i = 0; i < 43; i++)
        {
            source.Emit(Samples(320, 0.0));
        }

        VoiceTurnCaptureResult result = await capture.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(VoiceTurnStopReason.SpeechEnded, result.Reason);
        Assert.NotNull(result.WavBytes);
        Assert.False(service.IsCapturing);
        Assert.Equal(1, source.StopCalls);
    }

    [Fact]
    public async Task NoSpeechReturnsWithoutSendingAudio()
    {
        var source = new FakeVoiceFrameSource();
        var options = VoiceActivityOptions.Default with { MaxTurn = TimeSpan.FromMilliseconds(40) };
        var service = new VoiceTurnCaptureService(() => source, options);

        Task<VoiceTurnCaptureResult> capture = service.CaptureTurnAsync(CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        source.Emit(Samples(320, 0.0));
        source.Emit(Samples(320, 0.0));

        VoiceTurnCaptureResult result = await capture.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(VoiceTurnStopReason.NoSpeech, result.Reason);
        Assert.Null(result.WavBytes);
        Assert.Equal(1, source.StopCalls);
    }

    [Fact]
    public async Task CancellationReturnsCancelledAndStopsTheSource()
    {
        var source = new FakeVoiceFrameSource();
        var service = new VoiceTurnCaptureService(() => source);
        using var cancellation = new CancellationTokenSource();

        Task<VoiceTurnCaptureResult> capture = service.CaptureTurnAsync(cancellation.Token);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        VoiceTurnCaptureResult result = await capture.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(VoiceTurnStopReason.Cancelled, result.Reason);
        Assert.Equal(1, source.StopCalls);
        Assert.False(service.IsCapturing);
    }

    [Fact]
    public async Task ExplicitStopCancelsTheActiveCaptureIdempotently()
    {
        var source = new FakeVoiceFrameSource();
        var service = new VoiceTurnCaptureService(() => source);

        Task<VoiceTurnCaptureResult> capture = service.CaptureTurnAsync(CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await service.StopAsync();
        await service.StopAsync();

        VoiceTurnCaptureResult result = await capture.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(VoiceTurnStopReason.Cancelled, result.Reason);
        Assert.Equal(1, source.StopCalls);
        Assert.False(service.IsCapturing);
    }

    [Fact]
    public async Task RejectsConcurrentCaptures()
    {
        var source = new FakeVoiceFrameSource();
        var service = new VoiceTurnCaptureService(() => source);

        Task<VoiceTurnCaptureResult> first = service.CaptureTurnAsync(CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CaptureTurnAsync(CancellationToken.None));

        await service.StopAsync();
        await first.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SourceFaultReturnsCaptureError()
    {
        var source = new FakeVoiceFrameSource();
        var service = new VoiceTurnCaptureService(() => source);

        Task<VoiceTurnCaptureResult> capture = service.CaptureTurnAsync(CancellationToken.None);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        source.Fail(new InvalidOperationException("device unavailable"));

        VoiceTurnCaptureResult result = await capture.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(VoiceTurnStopReason.CaptureError, result.Reason);
        Assert.Null(result.WavBytes);
        Assert.Equal(1, source.StopCalls);
    }

    private static short[] Samples(int count, double amplitude)
    {
        short value = (short)Math.Round(Math.Clamp(amplitude, -1.0, 1.0) * short.MaxValue);
        return Enumerable.Repeat(value, count).ToArray();
    }

    private sealed class FakeVoiceFrameSource : IVoiceFrameSource
    {
        public event Action<ReadOnlyMemory<short>>? FrameReady;
        public event Action<Exception>? Faulted;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int StopCalls { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public void Emit(short[] samples) => FrameReady?.Invoke(samples);

        public void Fail(Exception exception) => Faulted?.Invoke(exception);

        public void Dispose()
        {
        }
    }
}
