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
    public void Batch_and_hold_timings_keep_progress_rows_readable()
    {
        // A burst of deltas is coalesced into one render pass...
        Assert.InRange(UiTiming.ActivityFlushBatch, TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(250));

        // ...but a fast-finishing row is still held long enough to be seen, which
        // only works if the hold outlasts the batch window.
        Assert.True(
            UiTiming.ActivityMinHold > UiTiming.ActivityFlushBatch,
            $"MinHold {UiTiming.ActivityMinHold} must exceed FlushBatch {UiTiming.ActivityFlushBatch}.");

        // The wave clock must be the fastest of the three so it animates within a hold.
        Assert.True(
            UiTiming.WaveFrame < UiTiming.ActivityMinHold,
            $"WaveFrame {UiTiming.WaveFrame} must be shorter than MinHold {UiTiming.ActivityMinHold}.");

        Assert.InRange(UiTiming.ActivityWavePeriodSeconds, 0.1, 5.0);
    }

    [Fact]
    public void Every_polling_interval_is_bounded_below_a_human_timescale()
    {
        // A poll faster than this would spin the backend; slower would read as broken.
        var polls = new[]
        {
            UiTiming.TypingDebounce,
            UiTiming.SessionsPoll,
            UiTiming.ReconnectPoll,
            UiTiming.ModelSaveFailure,
            UiTiming.ContextMeterDebounce,
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
        var code = GuiSource.Text("Views", "ChatView.xaml.cs");
        var handlers = GuiSource.ClickHandlers(document);
        Assert.NotEmpty(handlers);
        Assert.All(handlers, handler => Assert.True(
            GuiSource.HasMember(code, handler),
            $"Markup wires Click=\"{handler}\" but the code-behind does not define it."));
    }

    [Fact]
    public void Restoring_a_draft_is_scoped_per_session()
    {
        // Composer drafts are keyed by session, so switching chats must not carry
        // one session's half-typed message into another. The rule lives in
        // ComposerDraftStore and is asserted there; this checks the view reaches it
        // rather than keeping a map of its own.
        var code = GuiSource.Text("Views", "ChatView.xaml.cs");

        var restore = GuiSource.MethodBody(code, "RestoreComposerDraft");
        Assert.Contains("_composerDrafts", restore, StringComparison.Ordinal);

        // The view moves text; it does not decide what the text is. A body that reads
        // the box to answer that would be re-deriving the rule the store already owns.
        Assert.DoesNotContain("_composerDrafts.Save", restore, StringComparison.Ordinal);

        Assert.Contains("_composerDrafts.Clear", GuiSource.MethodBody(code, "ClearComposerDraft"), StringComparison.Ordinal);
        Assert.Contains("_composerDrafts.Save", GuiSource.MethodBody(code, "OnChatInputTyping"), StringComparison.Ordinal);
    }
}
