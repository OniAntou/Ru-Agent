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
    public void Reconciliation_required_renders_recovery_copy_instead_of_the_raw_code()
    {
        // A run parked on reconciliation is not terminal: the status bar must
        // explain why the agent stopped and what the user can do, not leak the
        // internal wire token.
        string rendered = AgentFailurePresentation.Render("reconciliation_required");

        Assert.NotEqual("reconciliation_required", rendered);
        Assert.Contains("Continue", rendered, StringComparison.Ordinal);
        Assert.Equal(
            rendered,
            AgentFailurePresentation.InlineFailureStatus(
                "reconciliation_required",
                "reconciliation_required",
                isRunning: true,
                hasRenderedReply: false));
    }

    [Fact]
    public void Worker_unavailable_explains_the_worker_instead_of_leaking_the_code_alone()
    {
        // Historical code: the model worker used to be its own process and could be missing
        // before any step runs. The copy still names the piece that was unreachable and that
        // a retry is the move, and it keeps the code in the sentence because the raw token
        // used to reach the reader and read like a fault in whatever they had just done.
        string rendered = AgentFailurePresentation.Render("worker_unavailable");

        Assert.NotEqual("worker_unavailable", rendered);
        Assert.Contains("worker_unavailable", rendered, StringComparison.Ordinal);
        Assert.Contains("again", rendered, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("invalid_workspace", "Invalid or non-existent workspace folder.")]
    [InlineData("workspace_busy", "Workspace is currently in use by another Agent session.")]
    [InlineData("preset_mismatch", "Permission preset has changed. Please select a preset and try again.")]
    [InlineData("preset_version_conflict", "Permission preset has changed. Please select a preset and try again.")]
    [InlineData("session_create_failed", "Failed to create a session for Coding Agent. Check backend and try again.")]
    [InlineData("session_not_found", "Chat session is no longer valid. Send a message to create a new session.")]
    [InlineData("invalid_session_id", "Chat session is no longer valid. Send a message to create a new session.")]
    [InlineData("unauthorized", "Agent session credentials are no longer valid. Please restart backend.")]
    [InlineData("agent_store_unavailable", "Agent database is temporarily unavailable. Check permissions or restart.")]
    [InlineData("harness_store_unavailable", "Agent database is temporarily unavailable. Check permissions or restart.")]
    public void Every_known_start_failure_has_its_own_sentence(string code, string expected)
    {
        Assert.Equal(expected, AgentFailurePresentation.RenderStartFailure(code, message: null));
    }

    [Fact]
    public void A_busy_workspace_names_the_blocking_run_when_the_server_sends_it()
    {
        string rendered = AgentFailurePresentation.RenderStartFailure(
            "workspace_busy", message: "Workspace busy.", blockingRunId: "run_abc", blockingStatus: "waiting_approval");

        Assert.Contains("run_abc", rendered, StringComparison.Ordinal);
        Assert.Contains("waiting_approval", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_busy_workspace_without_a_run_id_keeps_the_generic_sentence()
    {
        string rendered = AgentFailurePresentation.RenderStartFailure("workspace_busy", message: null);

        Assert.Equal("Workspace is currently in use by another Agent session.", rendered);
    }

    [Fact]
    public void An_unknown_start_failure_still_names_its_code()
    {
        // The code is the only handle support has; it must survive into the copy.
        string rendered = AgentFailurePresentation.RenderStartFailure("some_new_code", message: null);

        Assert.Contains("some_new_code", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_server_message_is_repeated_only_when_it_adds_something()
    {
        // Repeating the code as the message would read as a stutter.
        string stuttered = AgentFailurePresentation.RenderStartFailure(
            "some_new_code",
            message: "some_new_code");
        Assert.DoesNotContain("some_new_code: some_new_code", stuttered, StringComparison.Ordinal);

        string detailed = AgentFailurePresentation.RenderStartFailure(
            "some_new_code",
            message: "the disk is full");
        Assert.Contains("the disk is full", detailed, StringComparison.Ordinal);
    }

    [Fact]
    public void A_start_failure_without_a_code_still_says_something_actionable()
    {
        string rendered = AgentFailurePresentation.RenderStartFailure(errorCode: null, message: null);

        Assert.False(string.IsNullOrWhiteSpace(rendered));
        Assert.Contains("Activity Log", rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "  ")]
    public void A_blank_start_failure_never_renders_an_empty_status(string? code, string? message)
    {
        Assert.False(string.IsNullOrWhiteSpace(AgentFailurePresentation.RenderStartFailure(code, message)));
    }
}
