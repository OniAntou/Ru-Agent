using System.Collections.Concurrent;
using System.Threading;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class VoiceSubtitleEventPumpTests
{
    [Fact]
    public void DisabledPumpRejectsSubtitleWithoutChangingPresenter()
    {
        using var presenter = new SubtitlePresenter();
        using var pump = new VoiceSubtitleEventPump(presenter);

        Assert.False(pump.TryShow(new VoiceSubtitleCue("r1", "Cau mot")));
        Assert.Equal(SubtitleSnapshot.Empty, presenter.Current);
    }

    [Fact]
    public async Task TryShowReturnsWhilePresenterSubscriberIsBlocked()
    {
        using var presenter = new SubtitlePresenter();
        using var pump = new VoiceSubtitleEventPump(presenter);
        var rendererEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRenderer = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rendererExited = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.SnapshotChanged += snapshot =>
        {
            if (snapshot == SubtitleSnapshot.Empty)
            {
                return;
            }

            rendererEntered.TrySetResult(true);
            releaseRenderer.Task.GetAwaiter().GetResult();
            rendererExited.TrySetResult(true);
        };
        pump.SetEnabled(true);

        try
        {
            Assert.True(pump.TryShow(new VoiceSubtitleCue("r1", "Cau mot")));
            await rendererEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Task<bool> secondPublish = Task.Run(
                () => pump.TryShow(new VoiceSubtitleCue("r2", "Cau hai")));

            Assert.True(await secondPublish.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            releaseRenderer.TrySetResult(true);
            await rendererExited.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task FullMailboxDropsStaleStateAndKeepsLatestSubtitle()
    {
        using var presenter = new SubtitlePresenter();
        using var pump = new VoiceSubtitleEventPump(presenter);
        var snapshots = new ConcurrentQueue<string>();
        var firstRendererEntered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRenderer = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var latestRendered = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.SnapshotChanged += snapshot =>
        {
            if (snapshot == SubtitleSnapshot.Empty)
            {
                return;
            }

            snapshots.Enqueue(snapshot.Text);
            if (snapshot.Text == "Cau mot")
            {
                firstRendererEntered.TrySetResult(true);
                releaseFirstRenderer.Task.GetAwaiter().GetResult();
            }
            else if (snapshot.Text == "Cau ba")
            {
                latestRendered.TrySetResult(true);
            }
        };
        pump.SetEnabled(true);

        try
        {
            Assert.True(pump.TryShow(new VoiceSubtitleCue("r1", "Cau mot")));
            await firstRendererEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(pump.TryShow(new VoiceSubtitleCue("r2", "Cau hai")));
            Assert.True(pump.TryShow(new VoiceSubtitleCue("r3", "Cau ba")));
        }
        finally
        {
            releaseFirstRenderer.TrySetResult(true);
        }

        await latestRendered.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { "Cau mot", "Cau ba" }, snapshots.ToArray());
    }

    [Fact]
    public async Task SubscriberFailureDoesNotStopLaterSubtitleStates()
    {
        using var presenter = new SubtitlePresenter();
        using var pump = new VoiceSubtitleEventPump(presenter);
        int calls = 0;
        var firstCall = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCall = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        presenter.SnapshotChanged += _ =>
        {
            int current = Interlocked.Increment(ref calls);
            if (current == 1)
            {
                firstCall.TrySetResult(true);
            }
            else if (current == 2)
            {
                secondCall.TrySetResult(true);
            }

            throw new InvalidOperationException("renderer failed");
        };
        pump.SetEnabled(true);

        Assert.True(pump.TryShow(new VoiceSubtitleCue("r1", "Cau mot")));
        await firstCall.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(pump.TryShow(new VoiceSubtitleCue("r2", "Cau hai")));
        await secondCall.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(2, Volatile.Read(ref calls));
        Assert.Equal(new SubtitleSnapshot("Cau hai"), presenter.Current);
    }
}
