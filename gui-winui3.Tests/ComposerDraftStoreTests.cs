using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// Half-typed messages per conversation: what survives switching sessions, and what
/// a blank box means.
/// </summary>
public sealed class ComposerDraftStoreTests
{
    [Fact]
    public void A_session_without_a_draft_restores_empty()
    {
        var store = new ComposerDraftStore();

        Assert.Equal(string.Empty, store.Restore("session-a"));
        Assert.False(store.HasDraft("session-a"));
    }

    [Fact]
    public void Typing_then_leaving_then_returning_brings_the_text_back()
    {
        // The case the store exists for: the user half-writes a message, switches to
        // another conversation, and comes back.
        var store = new ComposerDraftStore();

        store.Save("session-a", "half a mes");
        store.Save("session-b", "something else");

        Assert.Equal("half a mes", store.Restore("session-a"));
        Assert.Equal("something else", store.Restore("session-b"));
    }

    [Fact]
    public void Drafts_are_kept_per_session_and_do_not_bleed()
    {
        var store = new ComposerDraftStore();

        store.Save("session-a", "first");
        store.Save("session-b", "second");

        Assert.Equal("first", store.Restore("session-a"));
        Assert.Equal("second", store.Restore("session-b"));
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void Emptying_the_box_drops_the_draft()
    {
        // An empty box is the absence of a draft; storing "" would make HasDraft lie.
        var store = new ComposerDraftStore();

        store.Save("session-a", "typed");
        Assert.True(store.HasDraft("session-a"));

        store.Save("session-a", string.Empty);

        Assert.False(store.HasDraft("session-a"));
        Assert.Equal(string.Empty, store.Restore("session-a"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_whitespace_only_draft_is_still_kept()
    {
        // Spaces are something the user typed deliberately while composing; dropping
        // them would silently eat the start of their next line.
        var store = new ComposerDraftStore();

        store.Save("session-a", "   ");

        Assert.Equal("   ", store.Restore("session-a"));
    }

    [Fact]
    public void Clearing_after_send_removes_only_that_session()
    {
        var store = new ComposerDraftStore();
        store.Save("session-a", "sent this");
        store.Save("session-b", "still typing");

        store.Clear("session-a");

        Assert.False(store.HasDraft("session-a"));
        Assert.Equal("still typing", store.Restore("session-b"));
    }

    [Fact]
    public void A_null_or_blank_session_id_is_ignored_rather_than_keyed()
    {
        var store = new ComposerDraftStore();

        store.Save(null, "text");
        store.Save(string.Empty, "text");
        store.Save("   ", "text");

        Assert.Equal(0, store.Count);
        Assert.Equal(string.Empty, store.Restore(null));
    }

    [Fact]
    public void A_replaced_session_carries_the_draft_to_the_new_id()
    {
        // The backend can replace a stale session underneath the user; the text on
        // screen belongs to whichever id the view is bound to now.
        var store = new ComposerDraftStore();
        store.Save("stale-session", "in progress");

        store.Move("stale-session", "fresh-session");

        Assert.False(store.HasDraft("stale-session"));
        Assert.Equal("in progress", store.Restore("fresh-session"));
    }

    [Fact]
    public void Moving_to_the_same_id_or_without_a_draft_changes_nothing()
    {
        var store = new ComposerDraftStore();
        store.Save("session-a", "keep me");

        store.Move("session-a", "session-a");
        store.Move("session-b", "session-c");
        store.Move(null, "session-d");

        Assert.Equal("keep me", store.Restore("session-a"));
        Assert.False(store.HasDraft("session-c"));
        Assert.Equal(1, store.Count);
    }
}
