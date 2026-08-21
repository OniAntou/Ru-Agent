using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoicePlaybackDeviceTests
{
    [Fact]
    public async Task Recording_device_records_playback_without_constructing_media_player()
    {
        using var events = new InMemoryHarnessEventSink();
        using var device = new RecordingVoicePlaybackDevice(events);

        await device.PlayAsync(BuildValidWav(), CancellationToken.None);

        Assert.Equal(1, device.PlayCalls);
        Assert.Equal(1, device.CompletedCalls);
        Assert.Equal(
            new[] { "voice.playback.started", "voice.playback.ended" },
            events.Codes);
    }

    [Fact]
    public async Task Recording_device_does_not_report_ended_after_cancellation()
    {
        using var events = new InMemoryHarnessEventSink();
        using var device = new RecordingVoicePlaybackDevice(
            events,
            playbackDelay: TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();

        Task playback = device.PlayAsync(BuildValidWav(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => playback);
        Assert.Equal(0, device.CompletedCalls);
        Assert.DoesNotContain("voice.playback.ended", events.Codes);
    }

    private static byte[] BuildValidWav()
    {
        var samples = Enumerable.Range(0, 3_200)
            .Select(index => (short)(index < 2_400 ? 1_000 : 0))
            .ToArray();
        return PcmWavEncoder.Encode(samples, 16_000, 1);
    }
}
