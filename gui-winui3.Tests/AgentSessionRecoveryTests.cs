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
    public void ChatView_waits_for_the_previous_agent_run_to_cancel_before_switching_session()
    {
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OpenSessionAsync");

        int stop = body.IndexOf("await StopActiveAgentAsync(force: true);", StringComparison.Ordinal);
        int switchSession = body.IndexOf("_activeSessionId = sessionId;", StringComparison.Ordinal);

        Assert.True(stop >= 0, "Switching sessions must await cancellation of the previous run.");
        Assert.True(switchSession >= 0, "OpenSessionAsync must set the active session.");
        Assert.True(
            stop < switchSession,
            "The cancellation must be awaited before the active session changes: a "
                + "fire-and-forget cancel races the next create request and surfaces as "
                + "\"workspace busy\".");
    }

    [Fact]
    public void The_outgoing_draft_is_saved_before_the_incoming_one_is_restored()
    {
        // Both halves key off the active session id, so saving after the switch would
        // file the outgoing text under the incoming conversation, and restoring before
        // it would look up the wrong id.
        var body = GuiSource.MethodBody(GuiSource.ViewText("ChatView"), "OpenSessionAsync");

        int save = body.IndexOf("Save(_activeSessionId", StringComparison.Ordinal);
        int switchSession = body.IndexOf("_activeSessionId = sessionId;", StringComparison.Ordinal);
        int restore = body.IndexOf("RestoreComposerDraft(sessionId)", StringComparison.Ordinal);

        Assert.True(save >= 0 && restore >= 0, "OpenSessionAsync must move the composer drafts.");
        Assert.True(save < switchSession, "The outgoing draft must be saved under the outgoing session id.");
        Assert.True(switchSession < restore, "The incoming draft must be restored after the switch.");
    }
}
