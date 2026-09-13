using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class AgentFailurePresentationTests
{
    [Fact]
    public void Provider_rate_limit_remains_visible_with_actionable_copy()
    {
        Assert.True(AgentFailurePresentation.ShouldKeepVisible(
            "failed",
            "provider_rate_limited"));
        Assert.Equal(
            "Model is rate-limited. Please select another model and try again.",
            AgentFailurePresentation.Render("provider_rate_limited"));
    }

    [Fact]
    public void Successful_terminal_run_does_not_keep_the_status_panel_visible()
    {
        Assert.False(AgentFailurePresentation.ShouldKeepVisible("completed", string.Empty));
    }

    [Fact]
    public void Repeated_tool_call_stays_visible_with_guidance_copy()
    {
        Assert.True(AgentFailurePresentation.ShouldKeepVisible(
            "failed",
            "repeated_tool_call"));
        Assert.Equal(
            "Ru repeated the same action instead of answering, so the run was stopped. Please rephrase your request and send it again.",
            AgentFailurePresentation.Render("repeated_tool_call"));
    }

    [Fact]
    public void Empty_model_reply_stays_visible_with_guidance_copy()
    {
        Assert.True(AgentFailurePresentation.ShouldKeepVisible(
            "failed",
            "empty_model_reply"));
        Assert.Equal(
            "The model returned an empty reply. Please try again, or select another model.",
            AgentFailurePresentation.Render("empty_model_reply"));
    }

    [Fact]
    public void Invalid_selection_stays_visible_with_guidance_copy()
    {
        Assert.True(AgentFailurePresentation.ShouldKeepVisible(
            "failed",
            "invalid_selection"));
        Assert.Equal(
            "Ru produced an action the host could not run, so the run was stopped. Please rephrase your request and send it again.",
            AgentFailurePresentation.Render("invalid_selection"));
    }

    [Fact]
    public void Failed_run_with_a_rendered_reply_keeps_the_inline_failure_copy()
    {
        Assert.Equal(
            AgentFailurePresentation.Render("repeated_tool_call"),
            AgentFailurePresentation.InlineFailureStatus(
                "failed",
                "repeated_tool_call",
                isRunning: false,
                hasRenderedReply: true));
    }

    [Fact]
    public void Failed_run_without_a_rendered_reply_defers_to_the_transcript_card()
    {
        Assert.Equal(
            string.Empty,
            AgentFailurePresentation.InlineFailureStatus(
                "failed",
                "repeated_tool_call",
                isRunning: false,
                hasRenderedReply: false));
    }

    [Fact]
    public void Running_failure_status_stays_visible_before_the_run_terminates()
    {
        Assert.Equal(
            AgentFailurePresentation.Render("repeated_tool_call"),
            AgentFailurePresentation.InlineFailureStatus(
                "failed",
                "repeated_tool_call",
                isRunning: true,
                hasRenderedReply: false));
    }

    [Fact]
    public void Successful_run_renders_no_inline_failure_copy()
    {
        Assert.Equal(
            string.Empty,
            AgentFailurePresentation.InlineFailureStatus(
                "completed",
                "Run ended.",
                isRunning: false,
                hasRenderedReply: true));
    }
}
