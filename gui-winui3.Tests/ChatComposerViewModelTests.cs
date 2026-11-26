using RuApp.Models;
using RuApp.Services;
using RuApp.ViewModels;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuApp.Tests;

public sealed class ChatComposerViewModelTests
{
    private static CreateRunProviderSelection Selection => new("models.dev:openai", "gpt-test");
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
    public void A_send_whose_workspace_trust_is_declined_comes_back_to_the_composer()
    {
        // B7: the composer clears when the Host accepts the run, before the trust
        // prompt. Declining trust ended the run and the typed message was simply gone.
        var shot = Image("shot.png");
        _composer.TryAddAttachment("sess-a", "fix the login bug", shot);
        var snapshot = _composer.TakeSnapshot("fix the login bug");
        _composer.ClearAfterSend("sess-a", snapshot);
        Assert.Equal(string.Empty, _replaced[^1]);
        Assert.Empty(_composer.Attachments);

        Assert.True(_composer.RestoreDeclinedSend("sess-a", liveText: string.Empty));

        Assert.Equal("fix the login bug", _replaced[^1]);
        Assert.Equal("shot.png", Assert.Single(_composer.Attachments).FileName);
        Assert.Equal("fix the login bug", _drafts.Restore("sess-a"));
        // Restored once: a second decline for the same send has nothing to bring back.
        Assert.False(_composer.RestoreDeclinedSend("sess-a", liveText: string.Empty));
    }

    [Fact]
    public void A_declined_send_never_overwrites_text_typed_since()
    {
        var snapshot = _composer.TakeSnapshot("first message");
        _composer.ClearAfterSend("sess-a", snapshot);
        _replaced.Clear();

        Assert.False(_composer.RestoreDeclinedSend("sess-a", liveText: "something newer"));

        Assert.Empty(_replaced);
    }

    [Fact]
    public void Only_the_session_the_send_belonged_to_gets_it_back_and_a_started_run_forgets_it()
    {
        var snapshot = _composer.TakeSnapshot("for session a");
        _composer.ClearAfterSend("sess-a", snapshot);

        Assert.False(_composer.RestoreDeclinedSend("sess-b", liveText: string.Empty));

        _composer.ForgetAcceptedSend("sess-a");
        Assert.False(_composer.RestoreDeclinedSend("sess-a", liveText: string.Empty));
    }

    [Fact]
    public void An_empty_composer_has_nothing_to_send_or_queue()
    {
        Assert.True(_composer.TakeSnapshot("   ").IsEmpty);
        Assert.False(_composer.QueueCurrent("sess-a", "   ", Selection));
        Assert.True(_composer.Queue.IsEmpty);
        Assert.Empty(_replaced);
    }

    [Fact]
    public void Queueing_moves_the_content_behind_the_run_and_says_so()
    {
        _composer.EditDraft("sess-a", "then this");

        Assert.True(_composer.QueueCurrent("sess-a", " then this ", Selection));

        var turn = Assert.Single(_composer.Queue.Items);
        Assert.Equal("then this", turn.Text);
        Assert.Equal(Selection, turn.ProviderSelection);
        Assert.Equal(string.Empty, _replaced[^1]);
        Assert.False(_drafts.HasDraft("sess-a"));
        Assert.Equal(AgentComposerPresentation.QueuedStatus, _runPanel.LocalStatus);
    }

    [Fact]
    public void A_full_queue_leaves_the_text_in_the_composer()
    {
        for (int i = 0; i < AgentTurnQueuePolicy.MaxTurns; i++)
        {
            Assert.True(_composer.QueueCurrent("sess-a", $"turn {i}", Selection));
        }

        _replaced.Clear();
        Assert.False(_composer.QueueCurrent("sess-a", "one too many", Selection));

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
        _composer.QueueCurrent("sess-a", "first", Selection);
        Assert.True(_composer.HasQueuedTurns);
        Assert.Equal("1 message queued", _composer.QueueHeaderText);
        Assert.False(_composer.CanSendQueueNow);

        _composer.QueueCurrent("sess-a", "second", Selection);
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
    public void A_composed_prompt_over_the_host_bound_is_reported_before_sending()
    {
        var document = new ImageAttachmentItem
        {
            FileName = "big.txt",
            IsImage = false,
            TextContent = new string('x', RuAgent.Protocol.Harness.AgentRunLimits.MaxPromptChars),
        };

        var prompt = ChatComposerViewModel.BuildEffectivePrompt("go", [document]);
        var status = ChatComposerViewModel.PromptLimitStatus(prompt);

        Assert.NotNull(status);
        Assert.Contains(RuAgent.Protocol.Harness.AgentRunLimits.MaxPromptChars.ToString("N0", System.Globalization.CultureInfo.InvariantCulture), status);
        Assert.Null(ChatComposerViewModel.PromptLimitStatus("short"));
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

    [Fact]
    public void A_workspace_reference_is_not_inlined_into_the_prompt()
    {
        var reference = new ImageAttachmentItem
        {
            FileName = "Program.cs",
            IsImage = false,
            ByteSize = 12 * 1024,
            ReferencePath = "src/Program.cs",
        };

        var prompt = ChatComposerViewModel.BuildEffectivePrompt("review", [reference]);

        Assert.Contains("[Attached workspace file: src/Program.cs (12.0 KB)]", prompt);
        Assert.Contains("not inlined", prompt);
        Assert.DoesNotContain("```", prompt);
        Assert.Null(ChatComposerViewModel.PromptLimitStatus(prompt));
    }

    [Fact]
    public void A_sent_user_bubble_shows_the_typed_text_and_an_attachment_only_turn_shows_none()
    {
        Assert.Equal("typed", ChatComposerViewModel.UserMessageContent("typed", "composed envelope"));
        Assert.Equal(string.Empty, ChatComposerViewModel.UserMessageContent(string.Empty, "composed envelope"));
        Assert.Equal("composed envelope", ChatComposerViewModel.UserMessageContent(null, "composed envelope"));
    }

    [Fact]
    public void The_same_file_is_not_attached_twice()
    {
        var text = new ImageAttachmentItem
        {
            FileName = "notes.txt",
            FilePath = @"C:\work\notes.txt",
            IsImage = false,
            TextContent = "hello",
        };
        Assert.True(_composer.TryAddAttachment("sess-a", string.Empty, text));

        var again = new ImageAttachmentItem
        {
            FileName = "notes.txt",
            FilePath = @"c:\WORK\notes.txt",
            IsImage = false,
            TextContent = "hello",
        };

        Assert.False(_composer.TryAddAttachment("sess-a", string.Empty, again));
        Assert.Single(_composer.Attachments);
        Assert.Contains("already attached", _runPanel.StatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Image_payloads_are_data_urls_and_a_missing_image_is_refused_locally()
    {
        var image = new ImageAttachmentItem
        {
            FileName = "shot.png",
            IsImage = true,
            MimeType = "image/png",
            Base64Data = "AAAA",
        };
        var document = new ImageAttachmentItem { FileName = "notes.txt", IsImage = false, TextContent = "x" };

        Assert.True(ChatComposerViewModel.TryBuildImagePayloads([image, document], out var payloads));
        Assert.Equal("data:image/png;base64,AAAA", Assert.Single(payloads!));

        // An image without normalized bytes used to fall back to a raw file path, which
        // the Host rejects with invalid_image_mime; the turn is refused before create now.
        Assert.False(ChatComposerViewModel.TryBuildImagePayloads([image, Image("no-bytes.png")], out _));

        Assert.True(ChatComposerViewModel.TryBuildImagePayloads(null, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Files_of_a_steer_that_did_not_leave_come_back_ahead_of_files_attached_since()
    {
        var notes = new ImageAttachmentItem { FileName = "notes.md", TextContent = "# plan", FilePath = @"C:\w\notes.md" };
        var later = new ImageAttachmentItem { FileName = "later.md", TextContent = "x", FilePath = @"C:\w\later.md" };
        Assert.True(_composer.TryAddAttachment("sess-a", string.Empty, later));

        _composer.ReturnAttachments("sess-a", "typed since", new[] { notes, later });

        Assert.Equal(new[] { "notes.md", "later.md" }, _composer.Attachments.Select(item => item.FileName));
        Assert.Equal(2, _drafts.RestoreAttachments("sess-a").Count);
    }

    private static ImageAttachmentItem Image(string name) => new() { FileName = name, IsImage = true };
}

public sealed class SteerAttachmentWiringTests
{
    [Fact]
    public void Enter_steers_with_files_and_images_and_queues_commands()
    {
        // Behavior: ChatComposerViewModelTests.Files_of_a_steer_that_did_not_leave_come_back_ahead_of_files_attached_since.
        string code = GuiSource.ViewText("ChatView");
        string steer = GuiSource.MethodBody(code, "SteerOrQueueCurrentComposerTurnAsync");
        Assert.Contains("if (text.StartsWith('/'))", steer, StringComparison.Ordinal);
        Assert.DoesNotContain("sent.ImageCount > 0", steer, StringComparison.Ordinal);
        Assert.Contains("ChatComposerViewModel.TryBuildImagePayloads(sent.Attachments, out var imagePayloads)", steer, StringComparison.Ordinal);
        // No attachments answers true with a null payload list, so a null-forgiving dereference
        // made Enter throw and do nothing for a plain message. Every use must accept null.
        Assert.DoesNotContain("imagePayloads!", steer, StringComparison.Ordinal);
        Assert.Contains("imagePayloads is { Count: > AgentSteerLimits.MaxImages }", steer, StringComparison.Ordinal);
        Assert.Contains("imagePayloads is not { Count: > 0 }", steer, StringComparison.Ordinal);
        Assert.Contains("ChatComposerViewModel.BuildEffectivePrompt(text, sent.Attachments)", steer, StringComparison.Ordinal);
        Assert.Contains("controller.SteerAsync(steerText, imagePayloads", steer, StringComparison.Ordinal);
        Assert.Contains("RestoreUnsentSteer(sessionId, sent)", steer, StringComparison.Ordinal);
        Assert.Contains("var waiting = SteerMessage(text, attachments);", GuiSource.MethodBody(code, "ShowWaitingSteer"), StringComparison.Ordinal);
        Assert.Contains("AttachedPromptDisplay.Apply(message);", code, StringComparison.Ordinal);
        Assert.Contains("AgentSteerText.Split(text)", code, StringComparison.Ordinal);
        string returned = GuiSource.MethodBody(code, "SendReturnedSteersAsync");
        Assert.Contains("AttachedPromptText.Join(", returned, StringComparison.Ordinal);
        Assert.Contains("ChatAttachmentPolicy.TakeImagesWithinTurnBound(images)", returned, StringComparison.Ordinal);
    }

    [Fact]
    public void Images_of_returned_steers_that_exceed_one_turn_are_cut_to_the_turn_bound()
    {
        var images = Enumerable.Range(0, 6)
            .Select(index => new ImageAttachmentItem { FileName = $"{index}.png", IsImage = true, Base64Data = "AAAA" })
            .ToList();

        var kept = ChatAttachmentPolicy.TakeImagesWithinTurnBound(images);

        Assert.Equal(images.Take(ChatAttachmentPolicy.MaxImageCount), kept);
    }
}

public sealed class DeclinedTrustWiringTests
{
    [Fact]
    public void The_trust_prompt_returns_a_declined_send_and_forgets_a_started_one()
    {
        // Behavior: ChatComposerViewModelTests.A_send_whose_workspace_trust_is_declined_comes_back_to_the_composer.
        string code = GuiSource.ViewText("ChatView");
        string prompt = GuiSource.MethodBody(code, "ShowWorkspaceTrustCardAsync");
        Assert.Contains("Composer.RestoreDeclinedSend(run.SessionId", prompt, StringComparison.Ordinal);
        Assert.Contains("Composer.ForgetAcceptedSend(run.SessionId)", prompt, StringComparison.Ordinal);
        Assert.Contains("Composer.ClearAfterSend(submittedSessionId, snapshot)", GuiSource.MethodBody(code, "SendCurrentMessageAsync"), StringComparison.Ordinal);
    }
}
