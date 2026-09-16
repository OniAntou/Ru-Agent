using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// The "run in hand" question: whether this session is busy, whether the send guard
/// is up, and which decision the user is actually being asked for.
///
/// These rules were previously spread across five call sites in ChatView, each
/// concatenating a different subset of a private flag and four controller
/// properties.
/// </summary>
public sealed class AgentRunActivityPolicyTests
{
    // ── Session binding ────────────────────────────────────────

    [Fact]
    public void A_run_for_another_session_does_not_count_for_this_one()
    {
        var state = AgentRunActivityPolicy.Evaluate(Input(
            runSessionId: "session-a",
            activeSessionId: "session-b",
            canCancel: true));

        Assert.False(state.IsGuarded);
        Assert.False(state.IsSessionBusy);
        Assert.Equal(AgentRunActivityReason.None, state.Reason);
    }

    [Fact]
    public void A_busy_controller_on_another_session_does_not_make_this_session_busy()
    {
        // The controller stays busy through the previous conversation's teardown;
        // those frames must not be painted into the chat now on screen.
        var state = AgentRunActivityPolicy.Evaluate(Input(
            runSessionId: "old-session",
            activeSessionId: "new-session",
            isBusy: true));

        Assert.False(state.IsSessionBusy);
    }

    [Fact]
    public void The_matching_session_is_bound()
    {
        Assert.True(AgentRunActivityPolicy.RunBelongsToSession("same", "same"));
    }

    [Theory]
    [InlineData(null, "session-b")]
    [InlineData("session-a", null)]
    [InlineData("session-a", "session-b")]
    public void A_missing_or_different_session_is_not_bound(string? runSessionId, string? activeSessionId)
    {
        Assert.False(AgentRunActivityPolicy.RunBelongsToSession(runSessionId, activeSessionId));
    }

    // ── The send guard ─────────────────────────────────────────

    [Fact]
    public void The_send_window_guards_even_though_no_run_exists_yet()
    {
        // The race this exists for: between tapping Send and the run being created,
        // every controller flag is still false, so a second Enter would otherwise
        // create a duplicate user bubble and a duplicate run.
        var state = AgentRunActivityPolicy.Evaluate(Input(
            isSendingTurn: true,
            hasRun: false,
            canCancel: false));

        Assert.True(state.IsGuarded);
        Assert.Equal(AgentRunActivityReason.Sending, state.Reason);
    }

    [Fact]
    public void The_send_window_is_not_session_bound()
    {
        // There is no run to compare against during the create await, so binding the
        // guard to a session id would drop it exactly when it is needed.
        var state = AgentRunActivityPolicy.Evaluate(Input(
            isSendingTurn: true,
            hasRun: false,
            runSessionId: null,
            activeSessionId: "session-a"));

        Assert.True(state.IsGuarded);
    }

    [Fact]
    public void The_send_window_shows_the_stop_affordance()
    {
        // The composer must already read as "running" during the await, or a second
        // tap would send rather than stop.
        Assert.True(AgentRunActivityPolicy.Evaluate(Input(isSendingTurn: true)).ShowsStopAffordance);
    }

    // ── The controller's own signals ───────────────────────────

    [Theory]
    [InlineData(nameof(AgentRunActivityInput.CanCancel), AgentRunActivityReason.Cancellable)]
    [InlineData(nameof(AgentRunActivityInput.HasPendingApproval), AgentRunActivityReason.AwaitingApproval)]
    [InlineData(nameof(AgentRunActivityInput.NeedsWorkspaceTrust), AgentRunActivityReason.NeedsTrust)]
    public void Each_controller_signal_alone_guards_the_session(string flag, AgentRunActivityReason expected)
    {
        var input = flag switch
        {
            nameof(AgentRunActivityInput.CanCancel) => Input(canCancel: true),
            nameof(AgentRunActivityInput.HasPendingApproval) => Input(hasPendingApproval: true),
            nameof(AgentRunActivityInput.NeedsWorkspaceTrust) => Input(needsWorkspaceTrust: true),
            _ => throw new ArgumentOutOfRangeException(nameof(flag)),
        };

        var state = AgentRunActivityPolicy.Evaluate(input);

        Assert.True(state.IsGuarded);
        Assert.Equal(expected, state.Reason);
    }

    [Fact]
    public void A_run_with_no_signal_does_not_guard()
    {
        var state = AgentRunActivityPolicy.Evaluate(Input(hasRun: true));

        Assert.False(state.IsGuarded);
        Assert.Equal(AgentRunActivityReason.None, state.Reason);
    }

    [Fact]
    public void The_reason_names_the_decision_the_user_is_being_asked_for()
    {
        // Approval outranks trust outranks cancel: the user is being asked to decide
        // something, and that is what the panel must say.
        Assert.Equal(
            AgentRunActivityReason.AwaitingApproval,
            AgentRunActivityPolicy.Evaluate(Input(
                canCancel: true,
                needsWorkspaceTrust: true,
                hasPendingApproval: true)).Reason);

        Assert.Equal(
            AgentRunActivityReason.NeedsTrust,
            AgentRunActivityPolicy.Evaluate(Input(
                canCancel: true,
                needsWorkspaceTrust: true)).Reason);
    }

    [Fact]
    public void Only_a_cancellable_run_shows_the_stop_affordance()
    {
        // An approval prompt and a trust request are answered in the panel, not by
        // stopping; showing Stop there would offer the wrong action.
        Assert.True(AgentRunActivityPolicy.Evaluate(Input(canCancel: true)).ShowsStopAffordance);
        Assert.False(AgentRunActivityPolicy.Evaluate(Input(hasPendingApproval: true)).ShowsStopAffordance);
        Assert.False(AgentRunActivityPolicy.Evaluate(Input(needsWorkspaceTrust: true)).ShowsStopAffordance);
    }

    // ── The unguarded question the send path needs ─────────────

    [Fact]
    public void The_controller_question_excludes_the_send_window()
    {
        // SendAgentTurnAsync already holds the send guard; asking the guarded question
        // there would block its own caller.
        var sending = Input(isSendingTurn: true, hasRun: false);

        Assert.True(AgentRunActivityPolicy.Evaluate(sending).IsGuarded);
        Assert.False(AgentRunActivityPolicy.IsControllerRunPending(sending));
    }

    [Fact]
    public void The_controller_question_is_session_bound_like_the_guarded_one()
    {
        Assert.False(AgentRunActivityPolicy.IsControllerRunPending(Input(
            runSessionId: "session-a",
            activeSessionId: "session-b",
            canCancel: true)));

        Assert.True(AgentRunActivityPolicy.IsControllerRunPending(Input(
            runSessionId: "session-a",
            activeSessionId: "session-a",
            canCancel: true)));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void The_controller_question_accepts_each_signal(
        bool canCancel,
        bool hasPendingApproval,
        bool needsWorkspaceTrust)
    {
        Assert.True(AgentRunActivityPolicy.IsControllerRunPending(Input(
            canCancel: canCancel,
            hasPendingApproval: hasPendingApproval,
            needsWorkspaceTrust: needsWorkspaceTrust)));
    }

    [Fact]
    public void No_run_at_all_means_no_controller_run_pending()
    {
        Assert.False(AgentRunActivityPolicy.IsControllerRunPending(Input(hasRun: false)));
    }

    private static AgentRunActivityInput Input(
        bool isSendingTurn = false,
        bool hasRun = true,
        string? runSessionId = "session-a",
        string? activeSessionId = "session-a",
        bool canCancel = false,
        bool hasPendingApproval = false,
        bool needsWorkspaceTrust = false,
        bool isBusy = false) =>
        new(
            isSendingTurn,
            hasRun,
            runSessionId,
            activeSessionId,
            canCancel,
            hasPendingApproval,
            needsWorkspaceTrust,
            isBusy);
}
