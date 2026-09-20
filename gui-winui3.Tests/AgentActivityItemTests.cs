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
    public void A_failed_tool_row_keeps_the_wording_it_ran_with()
    {
        // The row already names the action and the file beside it; failure changes the
        // state, and the colour carries that. Swapping the label for a failure sentence
        // renamed the row at the exact moment the reader was tracking it, so the file
        // that failed went unmentioned and the reason took its place.
        var writing = new AgentActivityItem(Activity(
            AgentActivityState.Failed,
            kind: AgentActivityKind.Edit,
            fileName: "script.js",
            toolDisplay: "Write",
            safeCode: "policy_denied"));

        Assert.True(writing.IsFailed);
        Assert.Equal("Writing", writing.DisplayText);
        Assert.Contains("script.js", writing.MetadataText, StringComparison.Ordinal);

        // The reason is not on the row at all any more: not in the label, not in the
        // muted line beside it.
        Assert.DoesNotContain("policy_denied", writing.MetadataText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_reason_row_still_states_its_reason()
    {
        // Status and Thinking rows name no action: they exist to report what happened,
        // so the reason is the whole message. Losing it would leave a cancelled or
        // unbuildable model call saying nothing.
        var model = new AgentActivityItem(Activity(
            AgentActivityState.Failed,
            kind: AgentActivityKind.Status,
            safeCode: "model_failed"));

        Assert.Equal("Model call failed", model.DisplayText);

        var cancelled = new AgentActivityItem(Activity(
            AgentActivityState.Failed,
            kind: AgentActivityKind.Thinking,
            safeCode: "cancelled"));

        Assert.Equal("Cancelled", cancelled.DisplayText);

        // A waiting row never names its reason in the headline, so its muted line keeps
        // the machine code it was carrying.
        var waiting = new AgentActivityItem(Activity(
            AgentActivityState.Waiting,
            safeCode: "terminal_unknown_executable"));

        Assert.Contains("terminal_unknown_executable", waiting.MetadataText, StringComparison.Ordinal);
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
        Assert.Equal("Ran terminal task", termCompleted.DisplayText);

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
    public void DisplayText_Prefers_the_command_for_a_terminal_row()
    {
        // The row names what ran, not what kind of thing it was: "Running Terminal task"
        // named the tool and left the reader to guess which command it belonged to.
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Terminal,
            toolDisplay: "Terminal",
            detail: "npm run test",
            durationMs: 2500));

        Assert.Equal("Running npm run test", item.DisplayText);
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
        // A failed compaction keeps its own wording too: the row said what it was doing,
        // and the state is what changed.
        Assert.Equal("Compacting context", compactionFailed.DisplayText);
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
    public void A_live_edit_starts_folded_and_only_the_reader_opens_it()
    {
        // A diff is the bulkiest thing the feed can carry, and the row's own header already
        // names the file it belongs to, so it waits to be asked for instead of unfolding
        // itself under every write the agent makes.
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Edit,
            detail: "@@ a ==> b"));

        Assert.False(item.IsEditPreviewExpanded);
        Assert.False(item.ShowEditPreview);

        Assert.True(item.UpdateFrom(Activity(AgentActivityState.Completed, kind: AgentActivityKind.Edit)));

        Assert.False(item.IsEditPreviewExpanded);
        Assert.False(item.ShowEditPreview);

        // Folding is not forgetting: the diff itself survives, which is what lets the reader
        // open it at all.
        Assert.True(item.HasEditPreview);
        Assert.Equal("a", item.EditBeforeText);

        item.IsEditPreviewExpanded = true;
        Assert.True(item.ShowEditPreview);
        Assert.Equal("\uE70D", item.EditPreviewChevronGlyph);
    }

    [Fact]
    public void A_replayed_edit_row_starts_folded()
    {
        // History is not a live turn: a settled row rebuilt from durable events has
        // nothing arriving to explain an open diff.
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Edit,
            detail: "@@ a ==> b"));

        Assert.True(item.HasEditPreview);
        Assert.False(item.ShowEditPreview);
    }

    [Fact]
    public void A_reader_who_toggles_the_preview_keeps_it()
    {
        var frame = Activity(AgentActivityState.Started, kind: AgentActivityKind.Edit, detail: "@@ a ==> b");
        var item = new AgentActivityItem(frame);

        item.IsEditPreviewExpanded = false;
        item.UpdateFrom(frame);

        // The reader's decision outranks later frames, or the row would reopen under
        // the click that closed it.
        Assert.False(item.IsEditPreviewExpanded);

        item.IsEditPreviewExpanded = true;
        Assert.True(item.ShowEditPreview);
    }

    [Fact]
    public void The_edit_row_header_is_its_toggle()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Edit,
            detail: "@@ a ==> b"));

        // The plain header stands down for a row whose header is a toggle: two headers
        // on one row, only one of them clickable, is the worse answer.
        Assert.False(item.ShowPlainHeader);

        // The chevron points right while the diff waits, and turns down once it is open.
        Assert.Equal("\uE76C", item.EditPreviewChevronGlyph);

        item.IsEditPreviewExpanded = true;
        Assert.Equal("\uE70D", item.EditPreviewChevronGlyph);

        var plain = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Terminal,
            detail: "ls"));

        Assert.True(plain.ShowPlainHeader);
        Assert.False(plain.HasEditPreview);
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
    public void Thinking_detail_starts_collapsed_and_toggles_open()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Thinking,
            detail: "considering folder scope"));

        // Collapsed by default so the deliberation never buries its own reply.
        Assert.True(item.IsThinking);
        Assert.True(item.HasThinkingDetail);
        Assert.False(item.IsThinkingExpanded);
        Assert.False(item.ShowThinkingDetail);
        // The plain line and the plain header stand down: the toggle owns both.
        Assert.False(item.ShowPlainDetail);
        Assert.False(item.ShowPlainHeader);
        Assert.Equal("\uE76C", item.ThinkingChevronGlyph);

        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        item.IsThinkingExpanded = true;

        Assert.True(item.ShowThinkingDetail);
        Assert.Equal("\uE70D", item.ThinkingChevronGlyph);
        Assert.Contains(nameof(AgentActivityItem.ShowThinkingDetail), changed);
        Assert.Contains(nameof(AgentActivityItem.ThinkingChevronGlyph), changed);
    }

    [Fact]
    public void Thinking_without_detail_has_no_toggle()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Thinking));

        Assert.True(item.IsThinking);
        Assert.False(item.HasThinkingDetail);
        Assert.False(item.ShowThinkingDetail);
        Assert.True(item.ShowPlainHeader);
    }

    [Fact]
    public void Non_thinking_detail_keeps_the_plain_line()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Waiting,
            kind: AgentActivityKind.Analyze,
            detail: "Considering folder scope."));

        Assert.False(item.IsThinking);
        Assert.False(item.HasThinkingDetail);
        Assert.True(item.ShowPlainDetail);
        Assert.True(item.ShowPlainHeader);
    }

    [Fact]
    public void A_terminal_row_carries_its_command_in_the_header_not_below_it()
    {
        // The command moved into the row's own words, so a muted copy underneath would say
        // the same thing twice on one line pair.
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Waiting,
            kind: AgentActivityKind.Terminal,
            detail: "npm run test"));

        Assert.True(item.HasDetail);
        Assert.False(item.ShowPlainDetail);
        Assert.Equal("npm run test", item.Detail);
    }

    [Fact]
    public void The_metadata_pill_is_the_row_s_own_words()
    {
        var edit = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Edit,
            fileName: "App.xaml",
            added: 2,
            removed: 1));

        Assert.True(edit.HasFileName);
        Assert.True(edit.HasMetadata);

        var search = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Search,
            symbol: "LoginButton",
            count: 3));

        Assert.False(search.HasFileName);
        Assert.True(search.HasMetadata);

        var bare = new AgentActivityItem(Activity(AgentActivityState.Started));

        Assert.False(bare.HasMetadata);
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
    /// A reason row has to say something the reader can act on.
    /// </summary>
    /// <remarks>
    /// It used to say "Failed" and nothing else, on every failure, while the row already
    /// carried the reason in its safe code. Two very different problems — a lost connection
    /// and a prompt the runtime refused — looked identical. The sentence now belongs to the
    /// rows that exist to explain (Status and Thinking); a tool row keeps the wording it
    /// ran with, which is asserted on its own below.
    /// </remarks>
    [Theory]
    [InlineData("interrupted", "Stopped before finishing")]
    [InlineData("model_failed", "Model call failed")]
    [InlineData("prompt_contract_invalid", "Prompt rejected by the runtime")]
    [InlineData("cancelled", "Cancelled")]
    [InlineData("transport_failure", "Lost connection to the tool")]
    [InlineData("reconciliation_required", "Stopped: the last change is unverified")]
    public void A_failure_names_its_reason_on_a_row_that_exists_to_explain(string safeCode, string expected)
    {
        var item = new AgentActivityItem(
            Activity(AgentActivityState.Failed, kind: AgentActivityKind.Status, safeCode: safeCode));

        Assert.Equal(expected, item.FailedText);
    }

    [Fact]
    public void An_unrecognised_failure_code_is_still_more_than_the_word_failed()
    {
        // A code this build does not know must not degrade to a blank stare. The token is
        // ugly, but it is a lead, and it is the only thing the row has.
        var item = new AgentActivityItem(
            Activity(AgentActivityState.Failed, kind: AgentActivityKind.Status, safeCode: "some_new_reason"));

        Assert.Equal("Failed: some new reason", item.FailedText);
    }

    [Fact]
    public void Distinct_failures_do_not_read_the_same()
    {
        // The property that actually matters: the reader can tell two failures apart.
        var lost = new AgentActivityItem(
            Activity(AgentActivityState.Failed, kind: AgentActivityKind.Status, safeCode: "transport_failure")).FailedText;
        var refused = new AgentActivityItem(
            Activity(AgentActivityState.Failed, kind: AgentActivityKind.Status, safeCode: "prompt_contract_invalid")).FailedText;

        Assert.NotEqual(lost, refused);
    }

    [Fact]
    public void A_status_row_that_only_says_ok_is_not_shown()
    {
        // A status row exists to explain something, and "ok" explains nothing: the step
        // around it either produced a row of its own or is visible in its absence.
        var ok = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Status,
            safeCode: "ok"));

        Assert.Equal("Status: ok", ok.DisplayText);
        Assert.False(ok.IsVisibleInFeed);

        var succeeded = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Status));

        Assert.False(succeeded.IsVisibleInFeed);
    }

    [Fact]
    public void A_status_row_that_explains_something_is_still_shown()
    {
        // These are the rows the kind exists for: they name a reason the reader cannot
        // infer from the rows around them.
        foreach (var code in new[] { "reconciliation_required", "inspection_required", "frames_dropped" })
        {
            var item = new AgentActivityItem(Activity(
                AgentActivityState.Completed,
                kind: AgentActivityKind.Status,
                safeCode: code));

            Assert.True(item.IsVisibleInFeed);
        }
    }

    [Fact]
    public void A_tool_row_is_not_silenced_by_its_outcome_code()
    {
        // An edit row says what it did, so the outcome code is not the row's content.
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Edit,
            fileName: "script.js",
            safeCode: "ok"));

        Assert.True(item.IsVisibleInFeed);
    }

    [Fact]
    public void A_terminal_row_names_the_command_it_ran_in_both_states()
    {
        // "Running Terminal task" and "Completed Terminal task" told the reader nothing about
        // which command either row was: two of them in a row read as one row printed twice.
        var running = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Terminal,
            toolDisplay: "Terminal",
            detail: "git status --porcelain=v2"));

        var completed = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Terminal,
            toolDisplay: "Terminal",
            detail: "git status --porcelain=v2"));

        Assert.Equal("Running git status --porcelain=v2", running.DisplayText);
        Assert.Equal("Ran git status --porcelain=v2", completed.DisplayText);

        // The command is the header now, so it is not repeated as a muted line under it.
        Assert.False(completed.ShowPlainDetail);
        Assert.True(completed.IsVisibleInFeed);
    }

    [Fact]
    public void A_command_that_spans_lines_names_its_row_by_its_first_line()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Started,
            kind: AgentActivityKind.Terminal,
            toolDisplay: "Terminal",
            detail: "dotnet test\n(output follows)"));

        Assert.Equal("Running dotnet test", item.DisplayText);
    }

    [Fact]
    public void A_terminal_row_with_no_command_still_says_it_is_a_terminal_row()
    {
        var item = new AgentActivityItem(Activity(
            AgentActivityState.Completed,
            kind: AgentActivityKind.Terminal,
            toolDisplay: "Terminal"));

        Assert.Equal("Ran terminal task", item.DisplayText);
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
