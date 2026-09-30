using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentFailureCopyTests
{
    // The audit list of codes that became terminal/visible on both surfaces, plus
    // the web and recovery codes enumerated from the Broker and the Host. Every
    // one of these must have cataloged copy, not fall through to a raw token.
    [Theory]
    [InlineData("policy_denied")]
    [InlineData("context_limit_reached")]
    [InlineData("model_step_budget_exhausted")]
    [InlineData("tool_call_budget_exhausted")]
    [InlineData("workspace_changed")]
    [InlineData("process_timed_out")]
    [InlineData("git_timed_out")]
    [InlineData("invalid_terminal_write_set")]
    [InlineData("unverified_after_tool_failure")]
    [InlineData("permission_profile_changed")]
    [InlineData("recovery_pre_dispatch")]
    [InlineData("recovery_read_only_incomplete")]
    [InlineData("commit_conflict")]
    [InlineData("broker_gateway_unavailable")]
    [InlineData("approval_authorization_failed")]
    [InlineData("invalid_preset")]
    [InlineData("output_frame_exceeds_limit")]
    [InlineData("run_not_parked")]
    [InlineData("trust_failed")]
    [InlineData("unsupported_protocol")]
    [InlineData("unsupported_protocol_version")]
    [InlineData("full_access_confirmation_required")]
    [InlineData("reconcile_failed")]
    [InlineData("web_response_too_large")]
    [InlineData("web_search_parse_failed")]
    [InlineData("web_redirect_limit")]
    [InlineData("web_redirect_invalid")]
    [InlineData("web_content_type_not_supported")]
    [InlineData("web_json_invalid")]
    [InlineData("web_encoding_invalid")]
    [InlineData("web_uri_not_allowed")]
    [InlineData("web_private_host")]
    [InlineData("web_connect_failed")]
    [InlineData("web_disabled")]
    [InlineData("web_uri_invalid")]
    [InlineData("web_search_endpoint_not_allowed")]
    public void Every_audited_code_resolves_to_copy(string code)
    {
        Assert.True(AgentFailureCopy.TryGet(code, out var copy), code);
        Assert.False(string.IsNullOrWhiteSpace(copy!.Title));
        Assert.False(string.IsNullOrWhiteSpace(copy.Cause));
        Assert.False(string.IsNullOrWhiteSpace(copy.Fix));
    }

    [Fact]
    public void Every_entry_carries_a_single_line_title_cause_and_fix()
    {
        Assert.NotEmpty(AgentFailureCopy.Table);

        foreach (var (code, copy) in AgentFailureCopy.Table)
        {
            Assert.False(string.IsNullOrWhiteSpace(code));
            Assert.Equal(code, code.Trim());
            Assert.False(string.IsNullOrWhiteSpace(copy.Title), code);
            Assert.False(string.IsNullOrWhiteSpace(copy.Cause), code);
            Assert.False(string.IsNullOrWhiteSpace(copy.Fix), code);
            Assert.DoesNotContain('\n', copy.Title);
            Assert.DoesNotContain('\n', copy.Cause);
            Assert.DoesNotContain('\n', copy.Fix);
        }
    }

    [Fact]
    public void Aliases_resolve_to_the_same_copy_as_their_primary_code()
    {
        var primary = AgentFailureCopy.Table["workspace_busy"];
        Assert.Same(primary, AgentFailureCopy.Table["session_busy"]);

        var rateLimited = AgentFailureCopy.Table["provider_rate_limited"];
        Assert.Same(rateLimited, AgentFailureCopy.Table["rate_limited"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("some_future_code")]
    public void Unknown_codes_do_not_resolve(string? code)
    {
        Assert.False(AgentFailureCopy.TryGet(code, out var copy));
        Assert.Null(copy);
    }
}
