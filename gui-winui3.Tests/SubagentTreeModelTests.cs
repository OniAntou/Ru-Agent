using RuAgent.Protocol.Subagents;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class SubagentTreeModelTests
{
    [Fact]
    public void Disposed_tree_releases_visual_lifecycle_callbacks_before_token_disposal()
    {
        var source = GuiSource.ViewText("SubagentTreeView");
        var dispose = source[source.IndexOf("public void Dispose()", StringComparison.Ordinal)..];
        // The parent disposes the tree before WinUI is guaranteed to raise Unloaded.
        Assert.Contains("Loaded -= OnLoaded", dispose, StringComparison.Ordinal);
        Assert.Contains("Unloaded -= OnUnloaded", dispose, StringComparison.Ordinal);
        Assert.True(dispose.IndexOf("Unloaded -=", StringComparison.Ordinal)
            < dispose.IndexOf("_lifetime.Dispose()", StringComparison.Ordinal));
    }

    private static SubagentListResponse Snapshot(string root, string id, string status = "running") =>
        new(root, status, id, [], [], 0, new(0, 0, null, null, null, false));

    [Fact]
    public void Root_changes_replays_and_out_of_order_responses_preserve_current_ownership()
    {
        var model = new SubagentTreeModel();
        model.Bind("root_one");
        var old = model.BeginRead();
        var current = model.BeginRead();
        Assert.True(model.Apply(current, Snapshot("root_one", "new")));
        Assert.False(model.Apply(old, Snapshot("root_one", "old")));
        Assert.False(model.Apply(model.BeginRead(), Snapshot("root_one", "new")));
        model.Bind("root_two");
        Assert.False(model.Apply(current, Snapshot("root_one", "stale")));
        Assert.Null(model.Snapshot);
        Assert.True(model.Apply(model.BeginRead(), Snapshot("root_two", "two", "completed")));
    }

    [Fact]
    public void Tree_and_settings_expose_no_child_mutation_controls()
    {
        var source = GuiSource.ViewText("SubagentTreeView");
        foreach (var action in new[] { "SendSubagentAsync", "CancelSubagentAsync", "ApplySubagentAsync", "RespondSubagentApprovalAsync", "OnSend(", "OnStop(", "OnApply(" })
            Assert.DoesNotContain(action, source, StringComparison.Ordinal);
        Assert.DoesNotContain("SubagentModels", GuiSource.ViewText("SettingsView"), StringComparison.Ordinal);
        Assert.Contains("GetSubagentAsync", source, StringComparison.Ordinal);
        Assert.Contains("OnExportResult", source, StringComparison.Ordinal);
        Assert.False(Snapshot("root", "five").Usage.IsComplete);
        Assert.Null(Snapshot("root", "five").Usage.TotalTokens);
    }
}
