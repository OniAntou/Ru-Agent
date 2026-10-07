using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentInlinePanelPolicyTests
{
    [Fact]
    public void Without_a_controller_everything_is_hidden()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input(hasController: false, isBusy: true));

        Assert.False(state.ShowInlinePanel);
        Assert.False(state.ShowApproval);
        Assert.False(state.ShowStop);
        Assert.False(state.IsRunning);
        Assert.Equal(string.Empty, state.StatusText);
    }

    [Fact]
    public void A_run_owned_by_another_session_never_paints_into_this_chat()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input(
            runBelongsToAnotherSession: true,
            isBusy: true,
            canCancel: true,
            hasPendingApproval: true,
            statusText: "working"));

        Assert.False(state.ShowInlinePanel);
        Assert.False(state.ShowApproval);
        Assert.False(state.IsRunning);
        Assert.Equal(string.Empty, state.StatusText);
    }

    [Fact]
    public void An_idle_turn_with_nothing_to_act_on_collapses()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input());

        Assert.False(state.ShowInlinePanel);
        Assert.False(state.IsRunning);
    }

    [Theory]
    [InlineData(nameof(AgentInlinePanelInput.IsBusy))]
    [InlineData(nameof(AgentInlinePanelInput.CanCancel))]
    [InlineData(nameof(AgentInlinePanelInput.HasPendingApproval))]
    [InlineData(nameof(AgentInlinePanelInput.HasPendingInput))]
    [InlineData(nameof(AgentInlinePanelInput.NeedsWorkspaceTrust))]
    [InlineData(nameof(AgentInlinePanelInput.CanContinue))]
    [InlineData(nameof(AgentInlinePanelInput.KeepTerminalFailureVisible))]
    public void Any_single_live_signal_keeps_the_panel_from_collapsing(string flag)
    {
        // Each of these is a reason the user still has something in front of them,
        // so none of them may be dropped by the idle branch.
        var input = flag switch
        {
            nameof(AgentInlinePanelInput.IsBusy) => Input(isBusy: true, statusText: "working"),
            nameof(AgentInlinePanelInput.CanCancel) => Input(canCancel: true, statusText: "working"),
            nameof(AgentInlinePanelInput.HasPendingApproval) => Input(hasPendingApproval: true, statusText: "working"),
            nameof(AgentInlinePanelInput.HasPendingInput) => Input(hasPendingInput: true, statusText: "working"),
            nameof(AgentInlinePanelInput.NeedsWorkspaceTrust) => Input(needsWorkspaceTrust: true, statusText: "working"),
            nameof(AgentInlinePanelInput.CanContinue) => Input(canContinue: true, statusText: "working"),
            nameof(AgentInlinePanelInput.KeepTerminalFailureVisible) => Input(keepTerminalFailureVisible: true, statusText: "working"),
            _ => throw new ArgumentOutOfRangeException(nameof(flag)),
        };

        var state = AgentInlinePanelPolicy.Evaluate(input);

        Assert.True(state.ShowInlinePanel, $"{flag} must keep the panel from collapsing.");
    }

    [Fact]
    public void The_removed_activity_visibility_flag_cannot_return()
    {
        // The flag drove an auto-expand behaviour that was deleted with its
        // guard; the empty consumer branch is gone too, so the projection must
        // not quietly grow the promise again.
        string code = GuiSource.Text("Services", "AgentInlinePanelPolicy.cs");

        Assert.DoesNotContain("ClearActivityVisibility", code, StringComparison.Ordinal);
    }

    [Fact]
    public void A_pending_approval_shows_the_approval_block_and_never_the_stop_control()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input(hasPendingApproval: true, isBusy: true));

        Assert.True(state.ShowApproval);
        Assert.True(state.ShowInlinePanel);
        Assert.False(state.ShowStop);
    }

    [Fact]
    public void A_pending_question_shows_the_question_block_and_never_the_stop_control()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input(hasPendingInput: true, isBusy: true));

        Assert.True(state.ShowQuestion);
        Assert.False(state.ShowApproval);
        Assert.True(state.ShowInlinePanel);
        Assert.False(state.ShowStop);
    }

    [Fact]
    public void A_resumable_run_keeps_the_panel_without_the_approval_block()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input(canContinue: true));

        Assert.True(state.ShowInlinePanel);
        Assert.False(state.ShowApproval);
    }

    [Fact]
    public void A_kept_terminal_failure_is_the_only_thing_left_and_still_shows()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input(
            keepTerminalFailureVisible: true,
            statusText: "Model is rate-limited. Please select another model and try again."));

        Assert.True(state.ShowInlinePanel);
        Assert.Equal("Model is rate-limited. Please select another model and try again.", state.StatusText);
    }

    [Fact]
    public void Status_copy_shows_only_when_the_panel_is_visible()
    {
        // A status line on a collapsed panel is what left stale text behind.
        var collapsed = AgentInlinePanelPolicy.Evaluate(Input(
            runBelongsToAnotherSession: true,
            statusText: "working"));

        Assert.False(collapsed.ShowInlinePanel);
        Assert.Equal(string.Empty, collapsed.StatusText);
    }

    [Fact]
    public void Pending_workspace_trust_is_actionable_for_the_active_run()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input(needsWorkspaceTrust: true, isBusy: true));

        Assert.True(ReadStateFlag(state, "ShowWorkspaceTrust"));
        Assert.True(state.ShowInlinePanel);
        Assert.False(state.ShowApproval);
        Assert.False(state.ShowStop);
    }

    [Fact]
    public void Workspace_trust_from_another_session_is_never_actionable()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input(
            runBelongsToAnotherSession: true,
            needsWorkspaceTrust: true,
            isBusy: true));

        Assert.False(ReadStateFlag(state, "ShowWorkspaceTrust"));
        Assert.False(state.ShowInlinePanel);
    }

    [Fact]
    public void A_workspace_trust_decision_in_flight_suppresses_interaction()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input(
            needsWorkspaceTrust: true,
            interactionPending: true));

        Assert.True(ReadStateFlag(state, "ShowWorkspaceTrust"));
        Assert.True(state.SuppressInteraction);
    }

    [Fact]
    public void The_state_exposes_an_explicit_workspace_trust_flag()
    {
        var stateType = typeof(AgentInlinePanelState);

        Assert.NotNull(stateType.GetProperty("ShowWorkspaceTrust"));
        Assert.Null(stateType.GetProperty("AutoApproveWorkspaceTrust"));
    }

    [Fact]
    public void Running_reads_from_the_controller_or_a_pending_run()
    {
        Assert.True(AgentInlinePanelPolicy.Evaluate(Input(canCancel: true)).IsRunning);
        Assert.True(AgentInlinePanelPolicy.Evaluate(Input(isBusy: true, runPending: true)).IsRunning);
        Assert.False(AgentInlinePanelPolicy.Evaluate(Input(canContinue: true)).IsRunning);
    }

    [Fact]
    public void A_busy_controller_with_only_a_pending_run_still_reads_as_running()
    {
        // The composer flips to the stop affordance as soon as a run is being
        // started, before the controller reports it as cancellable.
        var state = AgentInlinePanelPolicy.Evaluate(Input(
            isBusy: true,
            runPending: true,
            statusText: "Preparing action…"));

        Assert.True(state.IsRunning);
        Assert.True(state.ShowInlinePanel);
        Assert.Equal("Preparing action…", state.StatusText);
    }

    [Fact]
    public void An_in_flight_action_disables_every_decision_control()
    {
        var state = AgentInlinePanelPolicy.Evaluate(Input(
            hasPendingApproval: true,
            interactionPending: true));

        Assert.True(state.SuppressInteraction);
        Assert.True(state.ShowApproval, "Suppression disables the controls; it does not hide them.");
    }

    private static bool ReadStateFlag(AgentInlinePanelState state, string propertyName)
    {
        var property = state.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return (bool)property!.GetValue(state)!;
    }

    private static AgentInlinePanelInput Input(
        bool hasController = true,
        bool runBelongsToAnotherSession = false,
        bool isBusy = false,
        bool canCancel = false,
        bool hasPendingApproval = false,
        bool needsWorkspaceTrust = false,
        bool canContinue = false,
        bool keepTerminalFailureVisible = false,
        bool runPending = false,
        string statusText = "",
        bool interactionPending = false,
        bool hasPendingInput = false) =>
        new(
            hasController,
            runBelongsToAnotherSession,
            isBusy,
            canCancel,
            hasPendingApproval,
            hasPendingInput,
            needsWorkspaceTrust,
            canContinue,
            keepTerminalFailureVisible,
            runPending,
            statusText,
            interactionPending);
}
