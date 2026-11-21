using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentRetryCopyTests
{
    [Theory]
    [InlineData("tool_transport_error", "a tool transport error")]
    [InlineData("model_output_truncated", "a truncated reply")]
    [InlineData("model_commentary_only", "a commentary-only reply")]
    [InlineData("empty_model_reply", "an empty reply")]
    [InlineData("unverified_after_tool_failure", "an unverified tool result")]
    [InlineData("unverified_source_edit", "an unverified change")]
    [InlineData("open_todos", "open tasks")]
    [InlineData("completion_review", "a requirements check")]
    [InlineData("advisor_recommended", "an advisor recommendation")]
    [InlineData("invalid_selection", "a rejected tool selection")]
    [InlineData("model_tool_call_invalid", "a malformed tool call")]
    [InlineData("model_tool_call_unavailable", "an unavailable tool")]
    [InlineData("context_window_exceeded", "context pressure")]
    [InlineData("repeated_tool_call", "a repeated tool call")]
    [InlineData("provider_rate_limited", "a rate limit")]
    [InlineData("provider_timeout", "a timeout")]
    [InlineData("provider_server_error", "a server error")]
    [InlineData("provider_transport_error", "a transport error")]
    [InlineData("protocol_failure", "a malformed response")]
    public void Known_causes_keep_their_own_wording(string code, string expected)
    {
        Assert.Equal(expected, AgentRetryCopy.Reason(code));
    }

    [Fact]
    public void Started_and_completed_rows_say_why_the_step_repeats()
    {
        Assert.Equal("Retrying after a truncated reply (attempt 2)",
            AgentRetryCopy.Started("model_output_truncated", 2));
        Assert.Equal("Retrying after a truncated reply",
            AgentRetryCopy.Started("model_output_truncated", null));
        Assert.Equal("Retried after a truncated reply",
            AgentRetryCopy.Completed("model_output_truncated"));
        Assert.Equal("retrying after a truncated reply",
            AgentRetryCopy.Notice("model_output_truncated"));
    }

    [Fact]
    public void Unknown_and_legacy_codes_stay_visible_instead_of_becoming_another_cause()
    {
        Assert.Equal("an earlier failure (future_cause)", AgentRetryCopy.Reason("future_cause"));
        Assert.Equal("an earlier failure", AgentRetryCopy.Reason("retrying"));
        Assert.Null(AgentRetryCopy.Reason(null));
        Assert.Equal("Retrying after an earlier failure", AgentRetryCopy.Started(null, null));
    }
}
