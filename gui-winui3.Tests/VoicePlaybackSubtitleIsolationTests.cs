using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoicePlaybackSubtitleIsolationTests
{
    [Fact]
    public void ConfiguredSubtitlesStartDisabledAndTrackToggle()
    {
        using var api = new ApiService("test-secret", 65530);
        using var presenter = new SubtitlePresenter();
        using var service = new VoicePlaybackService(api);

        service.ConfigureSubtitles(presenter);
        Assert.False(service.SubtitlesEnabled);

        service.SubtitlesEnabled = true;
        Assert.True(service.SubtitlesEnabled);

        service.SubtitlesEnabled = false;
        Assert.False(service.SubtitlesEnabled);
    }

    [Fact]
    public async Task StopVoiceReturnsWhileSubtitleClearSubscriberIsBlocked()
    {
        using var api = new ApiService("test-secret", 65530);
        using var presenter = new SubtitlePresenter();
        using var service = new VoicePlaybackService(api);
        var clearEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseClear = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.ConfigureSubtitles(presenter);
        service.SubtitlesEnabled = true;
        presenter.BeginSegment("r1", "Cau mot");
        presenter.SnapshotChanged += snapshot =>
        {
            if (snapshot == SubtitleSnapshot.Empty)
            {
                clearEntered.TrySetResult(true);
                releaseClear.Task.GetAwaiter().GetResult();
            }
        };

        try
        {
            Task stop = Task.Run(service.StopVoice);
            await clearEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            await stop.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.True(stop.IsCompletedSuccessfully);
        }
        finally
        {
            releaseClear.TrySetResult(true);
        }
    }
}
