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
            "Ru repeated the same action instead of answering, so the run was stopped. Please rephrase your request or press Retry.",
            AgentFailurePresentation.Render("repeated_tool_call"));
    }

    [Fact]
    public void Invalid_selection_stays_visible_with_guidance_copy()
    {
        Assert.True(AgentFailurePresentation.ShouldKeepVisible(
            "failed",
            "invalid_selection"));
        Assert.Equal(
            "Ru produced an action the host could not run, so the run was stopped. Please rephrase your request or press Retry.",
            AgentFailurePresentation.Render("invalid_selection"));
    }
}
