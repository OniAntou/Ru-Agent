using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class OwnedUiUpdateTests
{
    [Fact]
    public async Task Session_or_turn_switch_before_dispatch_leaves_new_transcript_untouched()
    {
        var queue = new Queue<Action>();
        int currentGeneration = 1;
        int capturedGeneration = currentGeneration;
        string transcript = "new session";
        var apply = OwnedUiUpdate.ApplyAsync(action => { queue.Enqueue(action); return true; },
            () => currentGeneration == capturedGeneration, () => transcript = "old session");
        currentGeneration++;
        queue.Dequeue()();
        Assert.False(await apply);
        Assert.Equal("new session", transcript);
    }

    [Fact]
    public async Task Accepted_update_applies_and_rejected_dispatch_completes_without_mutation()
    {
        int applies = 0;
        Assert.True(await OwnedUiUpdate.ApplyAsync(null, () => true, () => applies++));
        Assert.False(await OwnedUiUpdate.ApplyAsync(_ => false, () => true, () => applies++));
        Assert.Equal(1, applies);
    }
}
