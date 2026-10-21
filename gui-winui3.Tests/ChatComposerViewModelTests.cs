using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatComposerViewModelTests
{
    private readonly AgentRunPanelViewModel _runPanel = new();
    private readonly ComposerDraftStore _drafts = new();
    private readonly List<string> _replaced = new();
    private readonly ChatComposerViewModel _composer;

    public ChatComposerViewModelTests()
    {
        _composer = new ChatComposerViewModel(_runPanel, _drafts);
        _composer.TextReplaced += _replaced.Add;
    }

    private static readonly string OverLimit = new('x', ComposerDraftStore.MaxTextBytesPerSession + 1);

    [Fact]
    public void Restoring_a_draft_is_scoped_per_session()
    {
        var shot = Image("shot.png");
        Assert.True(_composer.TryAddAttachment("sess-a", "half a thought", shot));
        _composer.EditDraft("sess-a", "half a thought");
        Assert.True(_composer.RetainDraft("sess-a", "half a thought", checkOnly: false));

        _composer.RestoreDraft("sess-b");
        Assert.Equal(string.Empty, _replaced[^1]);
        Assert.Empty(_composer.Attachments);
        Assert.False(_composer.HasAttachments);

        _composer.RestoreDraft("sess-a");
        Assert.Equal("half a thought", _replaced[^1]);
        Assert.Equal("shot.png", Assert.Single(_composer.Attachments).FileName);
        Assert.True(_composer.HasAttachments);
    }

    [Fact]
    public void Restoring_does_not_write_a_draft_of_its_own()
    {
        _composer.RestoreDraft("sess-a");

        Assert.False(_drafts.HasDraft("sess-a"));
    }

    [Fact]
    public void Typing_keeps_the_draft_and_the_retention_limit_is_shown_then_withdrawn()
    {
        _composer.EditDraft("sess-a", "hello");
        Assert.Equal("hello", _drafts.Restore("sess-a"));

        _composer.EditDraft("sess-a", OverLimit);
        Assert.Equal(ComposerDraftStore.RetentionLimitStatus, _runPanel.StatusText);
        Assert.Equal("hello", _drafts.Restore("sess-a"));

        _composer.EditDraft("sess-a", "hello again");
        Assert.Equal(string.Empty, _runPanel.LocalStatus);
        Assert.Equal("hello again", _drafts.Restore("sess-a"));
    }

    [Fact]
    public void A_keystroke_does_not_withdraw_another_composer_message()
    {
        _runPanel.ShowLocalStatus(AgentComposerPresentation.QueuedStatus);

        _composer.EditDraft("sess-a", "next");

        Assert.Equal(AgentComposerPresentation.QueuedStatus, _runPanel.LocalStatus);
    }

    [Fact]
    public void Leaving_a_session_checks_before_it_keeps_and_refuses_an_oversized_draft()
    {
        Assert.True(_composer.RetainDraft("sess-a", "kept", checkOnly: true));
        Assert.False(_drafts.HasDraft("sess-a"));

        Assert.True(_composer.RetainDraft("sess-a", "kept", checkOnly: false));
        Assert.Equal("kept", _drafts.Restore("sess-a"));

        Assert.False(_composer.RetainDraft("sess-a", OverLimit, checkOnly: true));
        Assert.Equal(ComposerDraftStore.RetentionLimitStatus, _runPanel.LocalStatus);
        Assert.Equal("kept", _drafts.Restore("sess-a"));
    }

    [Fact]
    public void A_send_clears_the_composer_only_while_it_still_holds_what_was_sent()
    {
        _composer.EditDraft("sess-a", "  ship it  ");
        var snapshot = _composer.TakeSnapshot("  ship it  ");

        Assert.Equal("ship it", snapshot.Text);
        Assert.True(_composer.IsUnchangedSince(snapshot, "  ship it  "));
        // Typed while the send awaited the Host: the new text must survive.
        Assert.False(_composer.IsUnchangedSince(snapshot, "  ship it  and also"));
        Assert.True(_composer.TryAddAttachment("sess-a", "  ship it  ", Image("late.png")));
        Assert.False(_composer.IsUnchangedSince(snapshot, "  ship it  "));

        _composer.ClearAfterSend("sess-a");
        Assert.Equal(string.Empty, _replaced[^1]);
        Assert.Empty(_composer.Attachments);
        Assert.False(_drafts.HasDraft("sess-a"));
    }

    [Fact]
    public void An_empty_composer_has_nothing_to_send_or_queue()
    {
        Assert.True(_composer.TakeSnapshot("   ").IsEmpty);
        Assert.False(_composer.QueueCurrent("sess-a", "   "));
        Assert.True(_composer.Queue.IsEmpty);
        Assert.Empty(_replaced);
    }

    [Fact]
    public void Queueing_moves_the_content_behind_the_run_and_says_so()
    {
        _composer.EditDraft("sess-a", "then this");

        Assert.True(_composer.QueueCurrent("sess-a", " then this "));

        var turn = Assert.Single(_composer.Queue.Items);
        Assert.Equal("then this", turn.Text);
        Assert.Equal(string.Empty, _replaced[^1]);
        Assert.False(_drafts.HasDraft("sess-a"));
        Assert.Equal(AgentComposerPresentation.QueuedStatus, _runPanel.LocalStatus);
    }

    [Fact]
    public void A_full_queue_leaves_the_text_in_the_composer()
    {
        for (int i = 0; i < AgentTurnQueuePolicy.MaxTurns; i++)
        {
            Assert.True(_composer.QueueCurrent("sess-a", $"turn {i}"));
        }

        _replaced.Clear();
        Assert.False(_composer.QueueCurrent("sess-a", "one too many"));

        Assert.Equal(AgentTurnQueuePolicy.MaxTurns, _composer.Queue.Items.Count);
        Assert.Empty(_replaced);
        Assert.Equal(AgentComposerPresentation.QueueFullStatus, _runPanel.LocalStatus);
    }

    [Fact]
    public void The_queue_strip_counts_turns_and_offers_send_now_only_without_a_run_in_hand()
    {
        Assert.False(_composer.HasQueuedTurns);
        Assert.False(_composer.CanSendQueueNow);

        _composer.SetRunInHand(true);
        _composer.QueueCurrent("sess-a", "first");
        Assert.True(_composer.HasQueuedTurns);
        Assert.Equal("1 message queued", _composer.QueueHeaderText);
        Assert.False(_composer.CanSendQueueNow);

        _composer.QueueCurrent("sess-a", "second");
        Assert.Equal("2 messages queued", _composer.QueueHeaderText);

        _composer.SetRunInHand(false);
        Assert.True(_composer.CanSendQueueNow);

        _composer.Queue.Clear();
        Assert.False(_composer.HasQueuedTurns);
        Assert.False(_composer.CanSendQueueNow);
    }

    [Fact]
    public void Images_past_the_turn_cap_are_refused_with_the_cap_shown()
    {
        for (int i = 0; i < ChatAttachmentPolicy.MaxImageCount; i++)
        {
            Assert.True(_composer.TryAddAttachment("sess-a", string.Empty, Image($"{i}.png")));
        }

        Assert.False(_composer.TryAddAttachment("sess-a", string.Empty, Image("extra.png")));
        Assert.Equal(ChatAttachmentPolicy.MaxImageCount, _composer.ImageCount);
        Assert.Equal(ChatAttachmentPolicy.TooManyImagesStatus, _runPanel.LocalStatus);

        // A document is not an image and still fits.
        Assert.True(_composer.TryAddAttachment("sess-a", string.Empty, new ImageAttachmentItem { FileName = "notes.txt", IsImage = false }));
    }

    [Fact]
    public void An_attachment_that_would_overflow_the_draft_is_not_added()
    {
        Assert.False(_composer.TryAddAttachment("sess-a", OverLimit, Image("shot.png")));

        Assert.Empty(_composer.Attachments);
        Assert.Equal(ComposerDraftStore.RetentionLimitStatus, _runPanel.LocalStatus);
    }

    [Fact]
    public void An_attachment_is_kept_under_the_session_its_intake_started_for()
    {
        Assert.True(_composer.TryAddAttachment("sess-intake", "text", Image("shot.png")));

        Assert.Single(_drafts.RestoreAttachments("sess-intake"));
    }

    [Fact]
    public void Removing_an_attachment_updates_the_draft()
    {
        var first = Image("a.png");
        _composer.TryAddAttachment("sess-a", "text", first);
        _composer.TryAddAttachment("sess-a", "text", Image("b.png"));

        _composer.RemoveAttachment("sess-a", "text", first.Id);
        _composer.RemoveAttachment("sess-a", "text", "missing");

        Assert.Equal("b.png", Assert.Single(_composer.Attachments).FileName);
        Assert.Equal("b.png", Assert.Single(_drafts.RestoreAttachments("sess-a")).FileName);
    }

    [Fact]
    public void The_effective_prompt_inlines_documents_above_the_text_and_leaves_images_out()
    {
        var document = new ImageAttachmentItem { FileName = "Notes.MD", IsImage = false, TextContent = "# plan", ByteSize = 6 };
        var prompt = ChatComposerViewModel.BuildEffectivePrompt("summarize", [Image("shot.png"), document]);

        Assert.Equal(
            $"[Attached file: Notes.MD ({document.FormattedSize})]{Environment.NewLine}```md{Environment.NewLine}# plan{Environment.NewLine}```{Environment.NewLine}{Environment.NewLine}summarize",
            prompt);
        Assert.Equal("only text", ChatComposerViewModel.BuildEffectivePrompt("only text", [Image("shot.png")]));
    }

    private static ImageAttachmentItem Image(string name) => new() { FileName = name, IsImage = true };
}
