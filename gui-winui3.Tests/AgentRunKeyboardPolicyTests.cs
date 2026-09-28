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
}
