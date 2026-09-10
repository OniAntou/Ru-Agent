using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityItemTests
{
    [Fact]
    public void Item_renders_safe_file_metadata_and_updates_state_text()
    {
        var started = Activity(
            AgentActivityState.Started,
            fileName: "ChatView.xaml.cs",
            lineStart: 150,
            lineEnd: 200);

        var item = new AgentActivityItem(started);

        Assert.True(item.IsRunning);
        Assert.Equal("Analyzing", item.DisplayText);
        Assert.Equal("ChatView.xaml.cs · L150–200", item.MetadataText);

        item.UpdateFrom(Activity(
            AgentActivityState.Completed,
            fileName: "ChatView.xaml.cs",
            lineStart: 150,
            lineEnd: 200,
            durationMs: 1200));

        Assert.True(item.IsCompleted);
        Assert.Equal("Analyzed", item.DisplayText);
        Assert.Equal("\uE73E", item.StatusGlyph);
    }

    [Fact]
    public void Item_keeps_terminal_text_bounded_to_safe_code()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Failed,
            safeCode: "policy_denied"));

        Assert.True(item.IsFailed);
        Assert.Equal("Failed · policy_denied", item.DisplayText);
    }

    [Fact]
    public void Item_renders_all_activity_kinds_in_english()
    {
        // Thinking
        var thinkingStarted = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Thinking));
        Assert.Equal("Thinking", thinkingStarted.DisplayText);

        var thinkingCompletedSubSecond = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Thinking, durationMs: 450));
        Assert.Equal("Thought for under 1 second", thinkingCompletedSubSecond.DisplayText);

        var thinkingCompletedSeconds = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Thinking, durationMs: 3000));
        Assert.Equal("Thought for 3 seconds", thinkingCompletedSeconds.DisplayText);

        var thinkingCompletedOneSecond = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Thinking, durationMs: 1000));
        Assert.Equal("Thought for 1 second", thinkingCompletedOneSecond.DisplayText);

        var thinkingCompletedNoDuration = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Thinking));
        Assert.Equal("Thought", thinkingCompletedNoDuration.DisplayText);

        // Explore
        var exploreStarted = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Explore, count: 5));
        Assert.Equal("Exploring 5 items", exploreStarted.DisplayText);

        var exploreStartedSingle = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Explore, count: 1));
        Assert.Equal("Exploring 1 item", exploreStartedSingle.DisplayText);

        var exploreCompleted = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Explore, count: 5));
        Assert.Equal("Explored 5 items", exploreCompleted.DisplayText);

        // Search
        var searchStarted = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Search, symbol: "OnSearch"));
        Assert.Equal("Searching for OnSearch", searchStarted.DisplayText);

        var searchCompleted = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Search, symbol: "OnSearch", count: 3));
        Assert.Equal("Searched for OnSearch · 3 results", searchCompleted.DisplayText);
        Assert.Equal("3 results", searchCompleted.MetadataText);

        var searchSingleResult = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Search, symbol: "OnSearch", count: 1));
        Assert.Equal("Searched for OnSearch · 1 result", searchSingleResult.DisplayText);
        Assert.Equal("1 result", searchSingleResult.MetadataText);

        // Edit
        var editStarted = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Edit, fileName: "App.xaml", added: 2, removed: 1));
        Assert.Equal("Editing", editStarted.DisplayText);
        Assert.Equal("App.xaml · +2 −1", editStarted.MetadataText);

        var editCompleted = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Edit));
        Assert.Equal("Edited", editCompleted.DisplayText);

        // Terminal
        var termStarted = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Terminal));
        Assert.Equal("Running terminal task", termStarted.DisplayText);

        var termCompleted = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Terminal));
        Assert.Equal("Completed terminal task", termCompleted.DisplayText);

        // Status / frames_dropped
        var statusDropped = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Status, safeCode: "frames_dropped"));
        Assert.Equal("Some steps were temporarily skipped", statusDropped.DisplayText);

        // Waiting
        var waitingStatus = new AgentActivityItem(Activity(AgentActivityState.Waiting, kind: AgentActivityKind.Status));
        Assert.Equal("Waiting for confirmation", waitingStatus.DisplayText);

        var waitingTool = new AgentActivityItem(Activity(AgentActivityState.Waiting, kind: AgentActivityKind.Edit, safeCode: "approval_required"));
        Assert.Equal("Waiting to continue · approval_required", waitingTool.DisplayText);
    }

    [Fact]
    public void DisplayText_Prefers_ToolDisplay_For_Started_Terminal()
    {
        var item = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Terminal, toolDisplay: "Terminal", durationMs: 2500));
        Assert.Contains("Terminal", item.DisplayText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Delete", "Deleting", "Deleted")]
    [InlineData("Restore", "Restoring", "Restored")]
    public void DisplayText_Uses_Delete_Restore_Verbs(string label, string started, string completed)
    {
        var running = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Edit, fileName: "obsolete.txt", toolDisplay: label));
        Assert.Equal(started, running.DisplayText);
        var done = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Edit, fileName: "obsolete.txt", toolDisplay: label));
        Assert.Equal(completed, done.DisplayText);
    }

    [Fact]
    public void Item_renders_compaction_and_retry_kinds()
    {
        var compactionStarted = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Compaction,
            count: 12));
        Assert.Equal("Compacting 12 context items", compactionStarted.DisplayText);

        var compactionSingle = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Compaction,
            count: 1));
        Assert.Equal("Compacting 1 context item", compactionSingle.DisplayText);

        var compactionCompleted = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Compaction,
            count: 12,
            durationMs: 1200));
        Assert.Equal("Compacted 12 context items", compactionCompleted.DisplayText);
        Assert.Equal("1.2s", compactionCompleted.MetadataText);

        var compactionFailed = new AgentActivityItem(Activity(
            AgentActivityState.Failed,
            kind: AgentActivityKind.Compaction,
            safeCode: "commit_conflict"));
        Assert.Equal("Failed · commit_conflict", compactionFailed.DisplayText);

        var retryStarted = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Retry,
            count: 2));
        Assert.Equal("Retrying (attempt 2)", retryStarted.DisplayText);

        var retryCompleted = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Retry,
            count: 2));
        Assert.Equal("Retried", retryCompleted.DisplayText);
    }

    [Fact]
    public void Item_adds_tool_duration_to_metadata()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Analyze,
            fileName: "README.md",
            durationMs: 250));

        Assert.Equal("README.md · 250ms", item.MetadataText);
    }

    private static AgentActivityModel Activity(
        AgentActivityState state,
        AgentActivityKind kind = AgentActivityKind.Analyze,
        string? fileName = null,
        int? lineStart = null,
        int? lineEnd = null,
        string? symbol = null,
        int? count = null,
        int? added = null,
        int? removed = null,
        int? durationMs = null,
        string safeCode = "succeeded",
        string? toolDisplay = null) =>
        new(
            "act_11111111111111111111111111111111",
            kind,
            state,
            fileName,
            lineStart,
            lineEnd,
            symbol,
            count,
            added,
            removed,
            durationMs,
            safeCode,
            toolDisplay,
            null,
            1);
}
