using RuAgent.Client;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentActivityItemTests
{
    [Fact]
    public void A_settled_row_keeps_its_producer_duration()
    {
        var item = new AgentActivityItem(Activity(AgentActivityState.Started));

        item.UpdateFrom(Activity(AgentActivityState.Completed, durationMs: 2_500));

        Assert.Equal("2.5s", item.DurationShortText);
    }

    [Fact]
    public void A_completed_frame_without_detail_keeps_the_started_edit_preview()
    {
        // Producer only sends @@ old ==> new on Started; Completed carries
        // Detail=null so Apply() must not wipe the preview the user is reading.
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Edit,
            detail: "@@ a ==> b"));

        Assert.True(item.HasEditPreview);

        item.UpdateFrom(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Edit));

        Assert.True(item.HasEditPreview);
        Assert.Equal("a", item.EditBeforeText);
        Assert.Equal("b", item.EditAfterText);
    }

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
    public void A_failed_row_hides_the_machine_code_it_already_translated()
    {
        // FailedText already turns the code into a sentence; repeating the raw
        // token in the muted line only adds noise. Waiting rows keep theirs:
        // their headline never names the reason.
        var failed = new AgentActivityItem(Activity(
            AgentActivityState.Failed,
            safeCode: "model_failed"));

        Assert.Equal("Model call failed", failed.DisplayText);
        Assert.DoesNotContain("model_failed", failed.MetadataText, StringComparison.Ordinal);

        var waiting = new AgentActivityItem(Activity(
            AgentActivityState.Waiting,
            safeCode: "terminal_unknown_executable"));

        Assert.Contains("terminal_unknown_executable", waiting.MetadataText, StringComparison.Ordinal);
    }

    [Fact]
    public void Item_keeps_terminal_text_bounded_to_safe_code()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Failed,
            safeCode: "policy_denied"));

        Assert.True(item.IsFailed);

        // The row states the reason and the machine token travels with the copy
        // action, so a reader gets a sentence and a supporter pastes the exact
        // code from one click.
        Assert.Equal("Failed: policy denied", item.DisplayText);
        Assert.Equal(string.Empty, item.MetadataText);
    }

    [Fact]
    public void Item_renders_all_activity_kinds_in_english()
    {
        // Thinking
        var thinkingStarted = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Thinking));
        Assert.Equal("Thinking", thinkingStarted.DisplayText);

        var thinkingCompletedSubSecond = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Thinking, durationMs: 450));
        Assert.Equal("Thought briefly", thinkingCompletedSubSecond.DisplayText);

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
        Assert.True(editStarted.HasLineDelta);
        Assert.False(editStarted.HasNoLineDelta);
        Assert.True(editStarted.HasLineDeltaAndBaseMetadata);
        Assert.Equal("App.xaml", editStarted.BaseMetadataText);
        Assert.Equal("+2", editStarted.AddedDeltaText);
        Assert.Equal("−1", editStarted.RemovedDeltaText);

        var editCompleted = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Edit));
        Assert.Equal("Edited", editCompleted.DisplayText);
        Assert.False(editCompleted.HasLineDelta);
        Assert.True(editCompleted.HasNoLineDelta);

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
        Assert.Equal("Waiting to continue", waitingTool.DisplayText);
        Assert.Equal("approval_required", waitingTool.MetadataText);
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
    [InlineData("Write", "Writing", "Wrote")]
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
        Assert.Equal(string.Empty, compactionCompleted.MetadataText);
        Assert.Equal("1.2s", compactionCompleted.DurationShortText);
        Assert.True(compactionCompleted.HasDuration);

        var compactionFailed = new AgentActivityItem(Activity(
            AgentActivityState.Failed,
            kind: AgentActivityKind.Compaction,
            safeCode: "commit_conflict"));
        Assert.Equal("Failed: commit conflict", compactionFailed.DisplayText);
        // The code stays out of the muted line: the headline translated it and
        // the copy action carries it for bug reports.
        Assert.Equal(string.Empty, compactionFailed.MetadataText);

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
    public void Item_omits_tool_duration_from_metadata()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Analyze,
            fileName: "README.md",
            durationMs: 250));

        Assert.Equal("README.md", item.MetadataText);
    }

    [Fact]
    public void Explore_labels_name_the_target_and_metadata_skips_the_duplicate_verb()
    {
        var exploring = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Explore,
            fileName: "Test Agent",
            toolDisplay: "Explore"));
        Assert.Equal("Exploring Test Agent", exploring.DisplayText);
        Assert.Equal(string.Empty, exploring.MetadataText);

        var explored = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Explore,
            fileName: "Test Agent",
            toolDisplay: "Explore",
            durationMs: 19));
        Assert.Equal("Explored Test Agent", explored.DisplayText);
        Assert.Equal(string.Empty, explored.MetadataText);
    }

    [Fact]
    public void Feed_rows_start_visible_with_no_expandable_details()
    {
        var item = new AgentActivityItem(Activity(AgentActivityState.Started));

        Assert.True(item.IsVisibleInFeed);
        Assert.Equal(0, item.CollapsedDurationMs);
    }

    [Fact]
    public void Waiting_and_failed_labels_surface_the_reason_inline()
    {
        var waiting = new AgentActivityItem(Activity(
            AgentActivityState.Waiting,
            kind: AgentActivityKind.Edit,
            fileName: "obsolete.txt",
            safeCode: "approval_required"));

        Assert.Contains("approval_required", waiting.MetadataText, StringComparison.Ordinal);

        var failed = new AgentActivityItem(Activity(
            AgentActivityState.Failed,
            kind: AgentActivityKind.Edit,
            safeCode: "policy_denied"));

        // The failed headline translated the code, so the muted line stays
        // quiet; the copy action carries the code instead.
        Assert.DoesNotContain("policy_denied", failed.MetadataText, StringComparison.Ordinal);
    }

    [Fact]
    public void Edit_started_preview_splits_into_before_and_after_panes()
    {
        var model = Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Edit,
            detail: "@@ line one\\nline two ==> line one\\nline TWO");
        var item = new AgentActivityItem(model);

        // The preview marker is what chooses the two-pane rendering, so the plain
        // detail line stands down: the row shows one or the other, never both.
        Assert.True(item.HasEditPreview);
        Assert.False(item.HasDetail);
        Assert.Equal("line one\nline two", item.EditBeforeText);
        Assert.Equal("line one\nline TWO", item.EditAfterText);
    }

    [Fact]
    public void A_running_edit_shows_its_preview()
    {
        // The regression this pins: the preview exists only on the Started frame, and
        // the row used to hide the detail of a running edit — so the diff for the edit
        // in front of the user was the one thing never rendered.
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Edit,
            detail: "@@ a ==> b"));

        Assert.True(item.IsRunning);
        Assert.True(item.HasEditPreview);
    }

    [Fact]
    public void An_edit_frame_without_a_preview_marker_shows_a_plain_detail()
    {
        // Completion frames carry no detail at all, but a frame that ever arrives with
        // ordinary text must render as text rather than as a broken two-pane preview.
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Edit,
            detail: "wrote 12 lines"));

        Assert.False(item.HasEditPreview);
        Assert.True(item.HasDetail);
        Assert.Equal(string.Empty, item.EditBeforeText);
        Assert.Equal(string.Empty, item.EditAfterText);
    }

    [Fact]
    public void HasDetail_Reflects_Detail_Presence_And_Notifies()
    {
        var without = new AgentActivityItem(Activity(AgentActivityState.Started));
        Assert.False(without.HasDetail);

        var changed = new List<string?>();
        without.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        without.UpdateFrom(Activity(AgentActivityState.Waiting, kind: AgentActivityKind.Status, detail: "paused for inspection"));

        Assert.True(without.HasDetail);
        Assert.Contains(nameof(AgentActivityItem.HasDetail), changed);
    }

    [Fact]
    public void Detail_Property_Tracks_Model_And_Notifies()
    {        var model = Activity(AgentActivityState.Waiting, kind: AgentActivityKind.Terminal, detail: "npm run test");
        var item = new AgentActivityItem(model);

        Assert.Equal("npm run test", item.Detail);

        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        var updated = Activity(AgentActivityState.Waiting, kind: AgentActivityKind.Terminal, detail: "git status");
        item.UpdateFrom(updated);

        Assert.Equal("git status", item.Detail);
        Assert.Contains(nameof(AgentActivityItem.Detail), changed);
    }

    [Fact]
    public void UpdateFrom_ignores_stale_started_frame_after_settle()
    {
        var item = new AgentActivityItem(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Edit, fileName: "a.txt"));

        var applied = item.UpdateFrom(Activity(AgentActivityState.Started, kind: AgentActivityKind.Edit, fileName: "a.txt"));

        Assert.False(applied);
        Assert.Equal(AgentActivityState.Completed, item.State);
    }

    [Fact]
    public void UpdateFrom_applies_newer_terminal_frame()
    {
        var item = new AgentActivityItem(Activity(AgentActivityState.Started, kind: AgentActivityKind.Edit, fileName: "a.txt"));

        var applied = item.UpdateFrom(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Edit, fileName: "a.txt"));

        Assert.True(applied);
        Assert.Equal(AgentActivityState.Completed, item.State);
    }

    /// <summary>
    /// A failed row has to say something the reader can act on.
    /// </summary>
    /// <remarks>
    /// It used to say "Failed" and nothing else, on every failure, while the row already
    /// carried the reason in its safe code. Two very different problems — a lost connection
    /// and a prompt the runtime refused — looked identical.
    /// </remarks>
    [Theory]
    [InlineData("interrupted", "Stopped before finishing")]
    [InlineData("model_failed", "Model call failed")]
    [InlineData("prompt_contract_invalid", "Prompt rejected by the runtime")]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("transport_failure", "Lost connection to the tool")]
    [InlineData("reconciliation_required", "Stopped: the last change is unverified")]
    public void A_failure_names_its_reason(string safeCode, string expected)
    {
        var item = new AgentActivityItem(
            Activity(AgentActivityState.Failed, safeCode: safeCode));

        Assert.Equal(expected, item.FailedText);
    }

    [Fact]
    public void An_unrecognised_failure_code_is_still_more_than_the_word_failed()
    {
        // A code this build does not know must not degrade to a blank stare. The token is
        // ugly, but it is a lead, and it is the only thing the row has.
        var item = new AgentActivityItem(
            Activity(AgentActivityState.Failed, safeCode: "some_new_reason"));

        Assert.Equal("Failed: some new reason", item.FailedText);
    }

    [Fact]
    public void Distinct_failures_do_not_read_the_same()
    {
        // The property that actually matters: the reader can tell two failures apart.
        var lost = new AgentActivityItem(
            Activity(AgentActivityState.Failed, safeCode: "transport_failure")).FailedText;
        var refused = new AgentActivityItem(
            Activity(AgentActivityState.Failed, safeCode: "prompt_contract_invalid")).FailedText;

        Assert.NotEqual(lost, refused);
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
        string? toolDisplay = null,
        string? detail = null) =>
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
            1,
            detail);
}
