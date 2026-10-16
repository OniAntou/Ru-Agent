using RuAgent.Client;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentSessionRecoveryTests
{
    [Fact]
    public void Recovery_is_one_shot_per_stale_session_and_resets_for_a_new_turn()
    {
        var recovery = new AgentSessionRecovery();

        Assert.True(recovery.TryBegin("stale-session"));
        Assert.False(recovery.TryBegin("stale-session"));
        Assert.True(recovery.TryBegin("another-session"));

        recovery.Reset();

        Assert.True(recovery.TryBegin("stale-session"));
    }

    [Theory]
    [InlineData("session_not_found")]
    [InlineData("invalid_session_id")]
    public void Only_stale_session_errors_are_recoverable(string errorCode)
    {
        Assert.True(AgentSessionRecovery.IsRecoverable(
            new AgentApiException(errorCode, "stale")));
        Assert.False(AgentSessionRecovery.IsRecoverable(
            new AgentApiException("workspace_busy", "busy")));
    }

    [Fact]
    public void ChatView_retries_a_stale_session_only_through_the_recovery_helper()
    {
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "SendAgentTurnAsync");

        // The view asks the helper whether this failure is a stale session; it carries no
        // list of codes of its own, which is what would drift from the API.
        Assert.Contains("AgentSessionRecovery.IsRecoverable", body, StringComparison.Ordinal);

        // The retry is gated by the caller's permission and by a one-shot claim, so a
        // second stale failure inside the retry cannot loop.
        Assert.Contains("allowSessionRecovery", body, StringComparison.Ordinal);
        Assert.Contains("_agentSessionRecovery.TryBegin", body, StringComparison.Ordinal);

        // And the retry disables the permission for itself.
        Assert.Contains("allowSessionRecovery: false", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_replaces_the_session_before_replaying_the_turn()
    {
        // Replaying against the session that just failed would fail the same way.
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "SendAgentTurnAsync");

        int recover = body.IndexOf("await RecoverAgentSessionAsync(", StringComparison.Ordinal);
        int replay = body.IndexOf("await SendAgentTurnAsync(", StringComparison.Ordinal);

        Assert.True(recover >= 0, "A stale session must be replaced.");
        Assert.True(replay >= 0, "The turn must be replayed once against the fresh session.");
        Assert.True(recover < replay, "The session must be replaced before the turn is replayed.");
    }

    [Fact]
    public void ChatView_delegates_switch_ownership_and_detaches_the_outgoing_feed()
    {
        // Stop-before-switch and latest-click behavior are exercised by
        // ChatSessionPresentationTests; this only pins the view's ownership seam.
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OpenSessionAsync");
        Assert.Contains("await _sessionPresentation.OpenAsync(", body, StringComparison.Ordinal);
        Assert.Contains("DetachAgentForSessionSwitchAsync", body, StringComparison.Ordinal);
        var detach = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "DetachAgentForSessionSwitchAsync");
        Assert.Contains("StopForSessionSwitchAsync", detach, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelAsync", detach, StringComparison.Ordinal);
        Assert.DoesNotContain("_activeSessionId =", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_view_restores_the_incoming_draft_after_the_owned_switch()
    {
        var code = GuiSource.ViewText("ChatView");
        var body = GuiSource.MethodBody(code, "OpenSessionAsync");
        int open = body.IndexOf("await _sessionPresentation.OpenAsync(", StringComparison.Ordinal);
        int restore = body.IndexOf("RestoreComposerDraft(sessionId)", StringComparison.Ordinal);
        Assert.True(open >= 0 && restore > open);
        Assert.Contains("RetainComposerDraft(checkOnly: false)", body, StringComparison.Ordinal);
        Assert.Contains("_composerDrafts.TrySave(_activeSessionId", GuiSource.MethodBody(code, "RetainComposerDraft"), StringComparison.Ordinal);
    }
}
