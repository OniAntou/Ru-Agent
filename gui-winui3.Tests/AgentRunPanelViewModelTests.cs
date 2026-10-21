using RuApp.Services;
using RuApp.ViewModels;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentRunPanelViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Enter_queues_while_a_run_is_in_hand_and_the_face_only_changes_on_a_flip()
    {
        var panel = new AgentRunPanelViewModel();
        var changes = new List<string?>();
        panel.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        panel.SetRunning(true);
        Assert.Equal(AgentComposerPresentation.QueuePlaceholder, panel.ComposerPlaceholder);
        Assert.Equal(AgentComposerPresentation.StopGlyph, panel.SendGlyph);
        Assert.Equal(AgentComposerPresentation.StopName, panel.SendName);

        changes.Clear();
        panel.SetRunning(true);
        Assert.DoesNotContain(nameof(AgentRunPanelViewModel.IsRunning), changes);

        panel.SetRunning(false);
        Assert.Equal(AgentComposerPresentation.MessagePlaceholder, panel.ComposerPlaceholder);
        Assert.Equal(AgentComposerPresentation.SendGlyph, panel.SendGlyph);
    }

    [Fact]
    public void A_local_message_survives_run_refreshes_until_it_is_cleared()
    {
        var panel = new AgentRunPanelViewModel();

        panel.ShowLocalStatus("File 'x.bin' is not a supported attachment.");
        panel.Evaluate(Input());

        Assert.True(panel.IsPanelVisible);
        Assert.Equal("File 'x.bin' is not a supported attachment.", panel.StatusText);

        panel.ClearLocalStatus();
        Assert.False(panel.IsPanelVisible);
        Assert.False(panel.IsStatusVisible);
    }

    [Fact]
    public void An_approval_takes_the_keyboard_once_per_approval_id()
    {
        var panel = new AgentRunPanelViewModel();
        int arrivals = 0;
        panel.ApprovalArrived += () => arrivals++;

        panel.Evaluate(Input(approval: Approval("appr_1")));
        panel.Evaluate(Input(approval: Approval("appr_1")));
        Assert.Equal(1, arrivals);
        Assert.Equal("appr_1", panel.Approval?.ApprovalId);

        panel.Evaluate(Input(approval: Approval("appr_2")));
        Assert.Equal(2, arrivals);

        panel.Evaluate(Input());
        Assert.Null(panel.Approval);
        panel.Evaluate(Input(approval: Approval("appr_2")));
        Assert.Equal(3, arrivals);
    }

    [Fact]
    public void A_question_takes_the_keyboard_once_per_input_id()
    {
        var panel = new AgentRunPanelViewModel();
        int arrivals = 0;
        panel.QuestionArrived += () => arrivals++;
        var question = new AgentQuestionPrompt("in_1", "Which file?", ["a", "b"]);

        panel.Evaluate(Input(question: question));
        panel.Evaluate(Input(question: question));

        Assert.Equal(1, arrivals);
        Assert.Equal("Which file?", panel.Question?.Question);
    }

    [Fact]
    public void Interaction_is_disabled_while_a_decision_is_being_submitted()
    {
        var panel = new AgentRunPanelViewModel();

        panel.Evaluate(Input(approval: Approval("appr_1"), interactionPending: true));

        Assert.False(panel.IsInteractionEnabled);
        panel.Evaluate(Input(approval: Approval("appr_1")));
        Assert.True(panel.IsInteractionEnabled);
    }

    [Fact]
    public void One_trust_prompt_per_run_and_a_prompt_that_could_not_open_is_retried_after_a_cooldown()
    {
        var panel = new AgentRunPanelViewModel();
        var controller = new object();

        Assert.True(panel.TryBeginTrustPrompt(controller, "run_1", "sess", Now));
        Assert.False(panel.TryBeginTrustPrompt(controller, "run_1", "sess", Now));

        // Another dialog was up: not answered, offered again after the cooldown only.
        Assert.True(panel.EndTrustPrompt(controller, "run_1", "sess", couldNotOpen: true, Now));
        Assert.False(panel.TryBeginTrustPrompt(controller, "run_1", "sess", Now));
        var later = Now + AgentRunPanelViewModel.TrustPromptRetryDelay;
        Assert.True(panel.TryBeginTrustPrompt(controller, "run_1", "sess", later));

        // Answered: the same run is never prompted again.
        Assert.True(panel.EndTrustPrompt(controller, "run_1", "sess", couldNotOpen: false, later));
        Assert.False(panel.TryBeginTrustPrompt(controller, "run_1", "sess", later.AddMinutes(1)));
    }

    [Fact]
    public void The_trust_guard_is_released_when_the_run_or_session_changes_and_on_reset()
    {
        var panel = new AgentRunPanelViewModel();
        var controller = new object();
        Assert.True(panel.TryBeginTrustPrompt(controller, "run_1", "sess", Now));
        panel.EndTrustPrompt(controller, "run_1", "sess", couldNotOpen: false, Now);

        panel.ForgetTrustIfRunChanged(controller, "run_1", "sess", "sess");
        Assert.False(panel.TryBeginTrustPrompt(controller, "run_1", "sess", Now));

        panel.ForgetTrustIfRunChanged(controller, "run_2", "sess", "sess");
        Assert.True(panel.TryBeginTrustPrompt(controller, "run_2", "sess", Now));

        panel.Reset();
        Assert.False(panel.IsTrustPromptOpen);
        Assert.True(panel.TryBeginTrustPrompt(controller, "run_2", "sess", Now));
    }

    [Fact]
    public void A_stale_prompt_completion_does_not_release_a_newer_guard()
    {
        var panel = new AgentRunPanelViewModel();
        var oldController = new object();
        var newController = new object();
        Assert.True(panel.TryBeginTrustPrompt(oldController, "run_1", "sess", Now));
        panel.Reset();
        Assert.True(panel.TryBeginTrustPrompt(newController, "run_2", "sess", Now));

        Assert.False(panel.EndTrustPrompt(oldController, "run_1", "sess", couldNotOpen: false, Now));
        Assert.True(panel.IsTrustPromptOpen);
    }

    [Fact]
    public void A_parked_trust_request_asks_for_a_prompt()
    {
        var panel = new AgentRunPanelViewModel();
        int requests = 0;
        panel.TrustPromptRequested += () => requests++;

        panel.Evaluate(Input(needsTrust: true));

        Assert.Equal(1, requests);
    }

    private static AgentApprovalPrompt Approval(string id) =>
        new(id, "Run cmd.exe", "cmd.exe /c echo hello", false, true, true);

    private static AgentRunPanelInput Input(
        AgentApprovalPrompt? approval = null,
        AgentQuestionPrompt? question = null,
        bool needsTrust = false,
        bool interactionPending = false) =>
        new(new AgentInlinePanelInput(
                HasController: true,
                RunBelongsToAnotherSession: false,
                IsBusy: approval is not null || question is not null || needsTrust,
                CanCancel: true,
                HasPendingApproval: approval is not null,
                HasPendingInput: question is not null,
                NeedsWorkspaceTrust: needsTrust,
                CanContinue: false,
                KeepTerminalFailureVisible: false,
                RunPending: false,
                StatusText: string.Empty,
                InteractionPending: interactionPending),
            approval,
            question);
}
