using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class UiCompletionOwnershipTests
{
    [Fact]
    public void QueuedCompletionCannotAffectReplacementOrShutdown()
    {
        var lifetime = new UiRequestLifetime();
        var old = lifetime.Replace();
        bool applied = false;
        Action queued = lifetime.Bind(old, () => applied = true);
        var replacement = lifetime.Replace();
        queued();
        Assert.False(applied);
        Assert.True(lifetime.Owns(replacement));
        lifetime.Replace();
        Assert.False(lifetime.Owns(replacement));
    }

    [Fact]
    public async Task ModelWritesAreOrderedAndQueuedIntermediateChoicesAreSkipped()
    {
        var queue = new ModelSelectionSaveQueue();
        var first = new TaskCompletionSource<bool>();
        var last = new TaskCompletionSource<bool>();
        var lastStarted = new TaskCompletionSource();
        var writes = new List<string>();
        var a = queue.SaveAsync(() => { writes.Add("a"); return first.Task; });
        var b = queue.SaveAsync(() => { writes.Add("b"); return Task.FromResult(true); });
        var c = queue.SaveAsync(() => { writes.Add("c"); lastStarted.SetResult(); return last.Task; });
        Assert.Equal(["a"], writes);
        first.SetResult(true);
        Assert.Equal(ModelSelectionSaveResult.Superseded, await a);
        Assert.Equal(ModelSelectionSaveResult.Superseded, await b);
        await lastStarted.Task;
        Assert.Equal(["a", "c"], writes);
        last.SetResult(true);
        Assert.Equal(ModelSelectionSaveResult.Saved, await c);
    }

    [Fact]
    public async Task SupersededFailureDoesNotPublishAndLatestFailureReleasesQueue()
    {
        var queue = new ModelSelectionSaveQueue();
        var first = new TaskCompletionSource<bool>();
        var a = queue.SaveAsync(() => first.Task);
        var b = queue.SaveAsync(() => throw new InvalidOperationException("synthetic failure"));
        first.SetResult(false);
        Assert.Equal(ModelSelectionSaveResult.Superseded, await a);
        Assert.Equal(ModelSelectionSaveResult.Failed, await b);
        Assert.Equal(ModelSelectionSaveResult.Saved, await queue.SaveAsync(() => Task.FromResult(true)));
    }

    [Fact]
    public async Task ShutdownSuppressesPendingSelectionAndUnstartedWrites()
    {
        var queue = new ModelSelectionSaveQueue();
        var first = new TaskCompletionSource<bool>();
        var a = queue.SaveAsync(() => first.Task);
        bool secondStarted = false;
        var b = queue.SaveAsync(() => { secondStarted = true; return Task.FromResult(true); });
        queue.Invalidate();
        first.SetResult(true);
        Assert.Equal(ModelSelectionSaveResult.Superseded, await a);
        Assert.Equal(ModelSelectionSaveResult.Superseded, await b);
        Assert.False(secondStarted);
    }
}
