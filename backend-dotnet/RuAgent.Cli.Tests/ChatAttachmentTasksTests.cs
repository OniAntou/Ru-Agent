using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatAttachmentTasksTests
{
    [Fact]
    public async Task StopWaitsForEveryRegisteredHandlerAndRefusesNewWork()
    {
        var owner = new ChatAttachmentTasks();
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = owner.RunAsync(() => firstRelease.Task);
        var second = owner.RunAsync(() => secondRelease.Task);
        var drained = owner.StopAsync();
        Assert.True(owner.HasPending);
        Assert.True(owner.IsStopped);
        Assert.False(drained.IsCompleted);
        var ranAfterStop = false;
        await owner.RunAsync(() => { ranAfterStop = true; return Task.CompletedTask; });
        Assert.False(ranAfterStop);
        secondRelease.SetResult();
        await second;
        Assert.False(drained.IsCompleted);
        firstRelease.SetResult();
        await first;
        await drained;
        Assert.False(owner.HasPending);
    }

    [Fact]
    public async Task CancellationMustFinishCleanupBeforeTheOwnerDrains()
    {
        var owner = new ChatAttachmentTasks();
        using var cancelled = new CancellationTokenSource();
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = owner.RunAsync(async () =>
        {
            try { await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancelled.Token); }
            finally { cleaning.SetResult(); await releaseCleanup.Task; }
        });
        cancelled.Cancel();
        await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drained = owner.StopAsync();
        Assert.True(owner.HasPending);
        Assert.False(drained.IsCompleted);
        releaseCleanup.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        await drained;
        Assert.False(owner.HasPending);
    }

    [Fact]
    public async Task SynchronousHandlerFailureIsObservableAndDoesNotLeakPendingWork()
    {
        var owner = new ChatAttachmentTasks();
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.RunAsync(() => throw new InvalidOperationException("synthetic")));
        Assert.False(owner.HasPending);
        await owner.StopAsync();
    }

    [Fact]
    public async Task WorkIsRegisteredBeforeItsSynchronousPrefixCanStopTheOwner()
    {
        var owner = new ChatAttachmentTasks();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? drained = null;
        var work = owner.RunAsync(() => { drained = owner.StopAsync(); return release.Task; });
        Assert.NotNull(drained);
        Assert.False(drained.IsCompleted);
        release.SetResult();
        await work;
        await drained;
    }
}
