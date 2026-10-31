using RuApp.Models;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class FileTreeRefreshControllerTests
{
    [Fact]
    public async Task Failed_refresh_retains_last_listing_only_for_the_same_workspace()
    {
        bool fail = false;
        var listing = new FileTreeListing([new("source", "A/source", true, true, 0)], false);
        var applied = new List<(FileTreeListing? Listing, bool Failed)>();
        var controller = new FileTreeRefreshController(_ => fail
            ? Task.FromException<FileTreeListing>(new IOException("folder unavailable"))
            : Task.FromResult(listing), (rows, failed) => applied.Add((rows, failed)));
        await controller.RefreshAsync("A");
        fail = true;
        await controller.RefreshAsync("A");
        Assert.Same(listing, applied[1].Listing);
        Assert.True(applied[1].Failed);
        await controller.RefreshAsync("B");
        Assert.Null(applied[2].Listing);
        Assert.True(applied[2].Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Workspace_switch_discards_old_result_or_error_and_loads_latest(bool failOld)
    {
        var gate = new TaskCompletionSource<FileTreeListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new List<string>();
        var applied = new List<(FileTreeListing? Listing, bool Failed)>();
        var newest = new FileTreeListing([FileTreeRow.Note("new workspace", 0)], false);
        var controller = new FileTreeRefreshController(root =>
        {
            requested.Add(root);
            return root == "A" ? gate.Task : Task.FromResult(newest);
        }, (listing, failed) => applied.Add((listing, failed)));
        var first = controller.RefreshAsync("A");
        await controller.RefreshAsync("B");
        await controller.RefreshAsync("C");
        if (failOld) gate.SetException(new IOException("old folder gone"));
        else gate.SetResult(new([], false));
        await first;
        Assert.Equal(["A", "C"], requested);
        Assert.Same(newest, Assert.Single(applied).Listing);
        Assert.False(applied[0].Failed);
    }

    [Fact]
    public async Task Clearing_workspace_during_load_cannot_repopulate_old_files()
    {
        var gate = new TaskCompletionSource<FileTreeListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<FileTreeListing?>();
        var controller = new FileTreeRefreshController(_ => gate.Task, (listing, _) => applied.Add(listing));
        var first = controller.RefreshAsync("A");
        await controller.RefreshAsync(null);
        gate.SetResult(new([FileTreeRow.Note("old", 0)], false));
        await first;
        Assert.Null(Assert.Single(applied));
        // A settled load releases the latch for a subsequent refresh too.
        await controller.RefreshAsync(null);
        Assert.Equal(2, applied.Count);
    }
}
