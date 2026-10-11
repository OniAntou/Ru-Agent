using System.Xml.Linq;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Chat "follow the newest message" behaviour: the shared timing constants that
/// drive it, and the new-messages pill that lets the user jump back to the bottom.
/// Asserted against the real constants and the markup tree, so tuning a timer in
/// one place cannot silently break the contract, and reordering markup cannot
/// break the test.
/// </summary>
public sealed class ChatFollowContractTests
{
    [Fact]
    public void Follow_threshold_is_a_usable_reading_window()
    {
        // Small enough that the view re-follows almost immediately, large enough
        // that a jittery wheel event does not yank the reader back down.
        Assert.InRange(UiTiming.ChatFollowThresholdPx, 24.0, 400.0);
    }

    [Fact]
    public void Batch_and_wave_timings_keep_progress_rows_readable()
    {
        // A burst of deltas is coalesced into one render pass, but the window must not
        // outlast the burst: measured against a real run the frames of a single tool
        // call span about 47ms, and a window longer than that held the whole call back
        // and rendered it in one lump. Above one 60Hz frame, below the shortest burst.
        Assert.InRange(UiTiming.ActivityFlushBatch, TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(32));

        // A lone frame skips the window entirely and renders on the next dispatcher
        // turn. There is deliberately no minimum hold on a completion: holding a
        // finished row is what made the feed stand still and then jump.
        Assert.True(
            UiTiming.WaveFrame < TimeSpan.FromMilliseconds(47),
            $"WaveFrame {UiTiming.WaveFrame} must be shorter than one tool-call burst.");

        Assert.InRange(UiTiming.ActivityWavePeriodSeconds, 0.1, 5.0);
    }

    [Fact]
    public void Every_polling_interval_is_bounded_below_a_human_timescale()
    {
        // A poll faster than this would spin the backend; slower would read as broken.
        var polls = new[]
        {
            UiTiming.SessionsPoll,
            UiTiming.ReconnectPoll,
            UiTiming.ModelSaveFailure,
            UiTiming.ContextMeterDebounce,
            UiTiming.GroupedPublishCoalesce,
            UiTiming.DiagnosticFilterDebounce,
        };

        Assert.All(polls, poll =>
            Assert.InRange(poll, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Chat_has_a_new_messages_pill_bound_to_a_jump_handler()
    {
        var document = GuiSource.Xml("Views", "ChatView.xaml");
        var named = GuiSource.NamedElements(document);

        Assert.True(named.ContainsKey("NewMessagesPill"), "NewMessagesPill must exist.");

        var pill = named["NewMessagesPill"];
        Assert.True(GuiSource.HasAttribute(pill, "AutomationId"), "The pill needs an automation id.");

        // It is reachable: every click handler the markup names is defined in the
        // code-behind. Searching the file for one handler name would pass on a stale
        // definition the markup no longer points at.
        var code = GuiSource.ViewText("ChatView");
        var handlers = GuiSource.ClickHandlers(document);
        Assert.NotEmpty(handlers);
        Assert.All(handlers, handler => Assert.True(
            GuiSource.HasMember(code, handler),
            $"Markup wires Click=\"{handler}\" but the code-behind does not define it."));
    }

    [Fact]
    public void Bulk_reload_never_scrolls_on_pre_reset_numbers()
    {
        // A ReplaceAll drops the virtualized panel before it measures: offsets
        // read synchronously right after it belong to the old content, so a
        // fresh open parked at its oldest message and a turn-end reload yanked
        // a parked reader to the top. Both paths settle after layout instead.
        var code = GuiSource.ViewText("ChatView");

        var open = GuiSource.MethodBody(code, "OpenSessionAsync");
        Assert.Contains("Messages.ReplaceAll(opened)", open, StringComparison.Ordinal);
        Assert.Contains("RunAfterLayout(ScrollToLatestAfterLayout)", open, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollChatToBottom()", open, StringComparison.Ordinal);

        var reload = GuiSource.MethodBody(code, "ReloadSessionHistoryAsync");
        Assert.Contains("preservedOffset", reload, StringComparison.Ordinal);
        Assert.Contains("Messages.ReplaceAll(restored)", reload, StringComparison.Ordinal);
        Assert.Contains("Math.Min(restore", reload, StringComparison.Ordinal);
    }

    [Fact]
    public void Deferred_layout_work_runs_low_priority_and_never_throws()
    {
        // MethodBody cannot tell the declaration apart from a call site that
        // passes a lambda, so this locates the declaration itself and reads a
        // bounded window: the three markers below are jointly distinctive of
        // the helper and of nothing else.
        var code = GuiSource.ViewText("ChatView");
        int declaration = code.IndexOf("private void RunAfterLayout(Action action)", StringComparison.Ordinal);
        Assert.True(declaration >= 0, "RunAfterLayout must be declared.");

        string window = code.Substring(declaration, Math.Min(1500, code.Length - declaration));
        Assert.Contains("DispatcherQueuePriority.Low", window, StringComparison.Ordinal);
        Assert.Contains("AppendTraceLine", window, StringComparison.Ordinal);
        Assert.Contains("ArgumentNullException.ThrowIfNull(action)", window, StringComparison.Ordinal);
    }

    [Fact]
    public void Restoring_a_draft_is_scoped_per_session()
    {
        // Composer drafts are keyed by session, so switching chats must not carry
        // one session's half-typed message into another. The rule lives in
        // ComposerDraftStore and is asserted there; this checks the view reaches it
        // rather than keeping a map of its own.
        var code = GuiSource.ViewText("ChatView");

        var restore = GuiSource.MethodBody(code, "RestoreComposerDraft");
        Assert.Contains("_composerDrafts", restore, StringComparison.Ordinal);
        Assert.Contains("_composerDrafts.RestoreAttachments", restore, StringComparison.Ordinal);

        // The view moves text; it does not decide what the text is. A body that reads
        // the box to answer that would be re-deriving the rule the store already owns.
        Assert.DoesNotContain("_composerDrafts.Save", restore, StringComparison.Ordinal);

        Assert.Contains("_composerDrafts.Clear", GuiSource.MethodBody(code, "ClearComposerDraft"), StringComparison.Ordinal);
        Assert.Contains("_composerDrafts.TrySave", GuiSource.MethodBody(code, "OnChatInputTyping"), StringComparison.Ordinal);
        Assert.Contains("_composerDrafts.TrySave", GuiSource.MethodBody(code, "OpenSessionAsync"), StringComparison.Ordinal);
    }
}
