using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>
/// What Escape means during a run, pinned.
/// </summary>
/// <remarks>
/// In a tool that executes shell commands, Escape is the key that reaches the app
/// instead of the text field, so it has one stated meaning rather than whatever the focus
/// happened to be. The precedence is the whole design and it is the kind of rule that
/// erodes quietly: a later change that checks "is a run going" before "is a question
/// open" would stop the run instead of answering the question, and the user would pay
/// for a decision they were in the middle of making.
/// </remarks>
public sealed class AgentRunKeyboardPolicyTests
{
    [Fact]
    public void Nothing_is_happening_so_escape_means_nothing()
    {
        Assert.Equal(
            EscapeOutcome.None,
            AgentRunKeyboardPolicy.Resolve(runInHand: false, approvalShowing: false));
    }

    [Fact]
    public void A_run_in_hand_makes_escape_stop_it()
    {
        Assert.Equal(
            EscapeOutcome.StopRun,
            AgentRunKeyboardPolicy.Resolve(runInHand: true, approvalShowing: false));
    }

    [Fact]
    public void An_open_approval_outranks_the_run()
    {
        // Denying costs a round trip: the agent asks again. Stopping costs the whole
        // turn, and it is the wrong answer to a question about one command.
        Assert.Equal(
            EscapeOutcome.DenyApproval,
            AgentRunKeyboardPolicy.Resolve(runInHand: true, approvalShowing: true));
    }

    [Fact]
    public void An_approval_outranks_a_run_even_when_the_run_flag_says_no()
    {
        // Between the run starting and the prompt appearing there is a window where the
        // controller says idle and the question is already on screen. A rule that checked
        // the run first would find nothing to stop and drop the key.
        Assert.Equal(
            EscapeOutcome.DenyApproval,
            AgentRunKeyboardPolicy.Resolve(runInHand: false, approvalShowing: true));
    }

    [Theory]
    [InlineData(false, false, EscapeOutcome.None)]
    [InlineData(true, false, EscapeOutcome.StopRun)]
    [InlineData(false, true, EscapeOutcome.DenyApproval)]
    [InlineData(true, true, EscapeOutcome.DenyApproval)]
    public void Every_combination_has_exactly_one_answer(bool run, bool approval, EscapeOutcome expected)
    {
        Assert.Equal(expected, AgentRunKeyboardPolicy.Resolve(run, approval));
    }

    [Fact]
    public void Popup_owns_escape_before_run_approval_or_question_policy()
    {
        foreach (bool flyout in new[] { false, true })
        foreach (bool run in new[] { false, true })
        foreach (bool approval in new[] { false, true })
        foreach (bool question in new[] { false, true })
        {
            EscapeOutcome expected = flyout
                ? EscapeOutcome.None
                : approval
                    ? EscapeOutcome.DenyApproval
                    : question || !run
                        ? EscapeOutcome.None
                        : EscapeOutcome.StopRun;

            Assert.Equal(expected, AgentRunKeyboardPolicy.Resolve(run, approval, question, flyout));
        }
    }

    [Fact]
    public void Both_chat_escape_callers_dismiss_owned_flyouts_before_run_policy()
    {
        string chat = GuiSource.ViewText("ChatView");
        string inputHandler = GuiSource.MethodBody(chat, "OnChatInputPreviewKeyDown");
        string surfaceHandler = GuiSource.MethodBody(chat, "OnChatSurfaceKeyDown");

        Assert.Contains("flyoutShowing: IsChatFlyoutShowing()", inputHandler, StringComparison.Ordinal);
        Assert.Contains("flyoutShowing: IsChatFlyoutShowing()", surfaceHandler, StringComparison.Ordinal);

        AssertOwnedFlyoutDismissesBeforePolicy(inputHandler);
        AssertOwnedFlyoutDismissesBeforePolicy(surfaceHandler);

        int defaultCase = inputHandler.IndexOf("default:", StringComparison.Ordinal);
        Assert.True(defaultCase >= 0);
        int defaultBreak = inputHandler.IndexOf("break;", defaultCase, StringComparison.Ordinal);
        Assert.True(defaultBreak > defaultCase);
        Assert.DoesNotContain("e.Handled", inputHandler[defaultCase..defaultBreak], StringComparison.Ordinal);

        int noStopBranch = surfaceHandler.IndexOf("if (escape != EscapeOutcome.StopRun)", StringComparison.Ordinal);
        Assert.True(noStopBranch >= 0);
        int stopHandled = surfaceHandler.IndexOf("e.Handled = true", noStopBranch, StringComparison.Ordinal);
        Assert.True(stopHandled > noStopBranch);
        Assert.Contains("return;", surfaceHandler[noStopBranch..stopHandled], StringComparison.Ordinal);
    }

    [Fact]
    public void Popup_focused_escape_is_handled_by_its_content_and_unhooked_on_close()
    {
        string keyboard = GuiSource.Text("Views", "ChatView.Keyboard.cs");
        string models = GuiSource.Text("Views", "ChatView.Models.cs");
        string xaml = GuiSource.Text("Views", "ChatView.xaml");
        string closed = GuiSource.MethodBody(keyboard, "OnChatOwnedFlyoutClosed");

        Assert.Contains("flyout.Content is not UIElement keyboardRoot", keyboard, StringComparison.Ordinal);
        Assert.Contains("keyboardRoot.AddHandler(", keyboard, StringComparison.Ordinal);
        Assert.Contains("handledEventsToo: true", keyboard, StringComparison.Ordinal);
        Assert.Contains("DismissChatOwnedFlyout(owner)", keyboard, StringComparison.Ordinal);
        Assert.Contains("flyout.Hide()", GuiSource.MethodBody(keyboard, "DismissChatOwnedFlyout"), StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", keyboard, StringComparison.Ordinal);
        Assert.Contains("keyboardRoot.RemoveHandler(", keyboard, StringComparison.Ordinal);
        Assert.Contains("keyboardRoot.IsTabStop = true", keyboard, StringComparison.Ordinal);
        Assert.Contains("keyboardRoot.Focus(FocusState.Programmatic)", keyboard, StringComparison.Ordinal);
        Assert.Contains("if (!flyout.IsOpen) DetachChatFlyoutEscape(flyout)", closed, StringComparison.Ordinal);
        Assert.Contains("AttachChatFlyoutEscape(ChatModelFlyout)", models, StringComparison.Ordinal);
        Assert.Contains("OnChatOwnedFlyoutClosed(ChatModelFlyout)", models, StringComparison.Ordinal);
        Assert.Contains("AttachChatFlyoutEscape(ChatReasoningFlyout)", models, StringComparison.Ordinal);
        Assert.Contains("OnChatOwnedFlyoutClosed(ChatReasoningFlyout)", models, StringComparison.Ordinal);
        Assert.Contains("Opened=\"OnChatModelFlyoutOpened\" Closed=\"OnChatModelFlyoutClosed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Opened=\"OnChatReasoningFlyoutOpened\" Closed=\"OnChatReasoningFlyoutClosed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Opened=\"OnContextWindowFlyoutOpened\" Closed=\"OnContextWindowFlyoutClosed\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AttachChatFlyoutEscape(ContextWindowFlyout)", models, StringComparison.Ordinal);
        Assert.Contains("OnChatOwnedFlyoutClosed(ContextWindowFlyout)", models, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_hover_cannot_reopen_after_escape_until_the_pointer_really_exits()
    {
        string chat = GuiSource.ViewText("ChatView");
        string keyboard = GuiSource.Text("Views", "ChatView.Keyboard.cs");
        string timer = GuiSource.MethodBody(chat, "OnContextHoverTimerTick");
        string enter = GuiSource.MethodBody(chat, "OnContextMeterPointerEntered");
        string exit = GuiSource.MethodBody(chat, "OnContextMeterPointerExited");
        string dismiss = GuiSource.MethodBody(keyboard, "DismissChatOwnedFlyout");
        string tryDismiss = GuiSource.MethodBody(keyboard, "TryDismissChatOwnedFlyout");
        string popupKeyDown = GuiSource.MethodBody(keyboard, "OnChatFlyoutKeyDown");

        Assert.Contains("_contextHoverState.CanOpen", timer, StringComparison.Ordinal);
        Assert.Contains("_contextHoverState.ObservePointer(inside: true)", enter, StringComparison.Ordinal);
        Assert.Contains("if (!_contextHoverState.CanOpen) return", enter, StringComparison.Ordinal);
        Assert.Contains("e.GetCurrentPoint(ContextMeterButton).Position", exit, StringComparison.Ordinal);
        Assert.Contains("_contextHoverState.ObservePointer(pointerStillInside)", exit, StringComparison.Ordinal);
        Assert.Contains("if (pointerStillInside) return", exit, StringComparison.Ordinal);
        Assert.Contains("_contextHoverTimer?.Stop()", dismiss, StringComparison.Ordinal);
        Assert.Contains("_contextHoverState.Dismiss()", dismiss, StringComparison.Ordinal);
        Assert.Contains("flyout.Hide()", dismiss, StringComparison.Ordinal);
        Assert.Contains("DismissChatOwnedFlyout(ContextWindowFlyout)", tryDismiss, StringComparison.Ordinal);
        Assert.Contains("DismissChatOwnedFlyout(owner)", popupKeyDown, StringComparison.Ordinal);
    }

    private static void AssertOwnedFlyoutDismissesBeforePolicy(string handler)
    {
        int dismiss = handler.IndexOf("if (TryDismissChatOwnedFlyout())", StringComparison.Ordinal);
        int resolve = handler.IndexOf("AgentRunKeyboardPolicy.Resolve", StringComparison.Ordinal);
        Assert.True(dismiss >= 0 && resolve > dismiss);
        string dismissBranch = handler[dismiss..resolve];
        Assert.Contains("e.Handled = true", dismissBranch, StringComparison.Ordinal);
        Assert.Contains("return;", dismissBranch, StringComparison.Ordinal);
    }
}
