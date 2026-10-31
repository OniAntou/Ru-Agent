using RuAgent.Client;
using Xunit;

namespace RuAgent.Client.Tests;

public sealed class AgentParkReasonCopyTests
{
    // The park reasons a root run reports while it stays alive for Continue. A
    // provider park without copy is the case that showed a raw code where the
    // user needed to read "network connection failed".
    [Theory]
    [InlineData("provider_transport_error")]
    [InlineData("provider_timeout")]
    [InlineData("provider_server_error")]
    [InlineData("provider_rate_limited")]
    [InlineData("provider_quota_exhausted")]
    [InlineData("model_step_budget_exhausted")]
    [InlineData("tool_call_budget_exhausted")]
    [InlineData("subagent_family_budget_exhausted")]
    [InlineData("context_limit_reached")]
    [InlineData("repeated_tool_call")]
    [InlineData("reconciliation_required")]
    [InlineData("recovery_ambiguous")]
    public void Every_park_code_resolves_to_a_headline_and_advice(string code)
    {
        Assert.True(AgentParkReasonCopy.TryGet(code, out var copy), code);
        Assert.False(string.IsNullOrWhiteSpace(copy!.Headline), code);
        Assert.False(string.IsNullOrWhiteSpace(copy.Advice), code);
        Assert.DoesNotContain('\n', copy.Headline);
        Assert.DoesNotContain('\n', copy.Advice);
        Assert.InRange(copy.Headline.Length, 1, 120);
        Assert.InRange(copy.Advice.Length, 1, 120);
    }

    [Fact]
    public void Network_drop_is_worded_as_a_connection_failure_not_a_wait_state()
    {
        Assert.Equal(
            "Paused: Network connection to the provider failed. Check your network first. Send Continue when ready.",
            AgentParkReasonCopy.Compose("provider_transport_error"));
        Assert.Equal(
            "Paused: Network connection to the provider failed",
            AgentParkReasonCopy.Headline("provider_transport_error"));
    }

    [Fact]
    public void Reconciliation_codes_share_one_copy()
    {
        Assert.Same(
            AgentParkReasonCopy.Table["reconciliation_required"],
            AgentParkReasonCopy.Table["recovery_ambiguous"]);
    }

    [Fact]
    public void Unknown_codes_keep_their_raw_token_at_the_caller()
    {
        Assert.False(AgentParkReasonCopy.TryGet("some_future_park", out var copy));
        Assert.Null(copy);
        Assert.Equal(string.Empty, AgentParkReasonCopy.Compose("some_future_park"));
        Assert.Equal(string.Empty, AgentParkReasonCopy.Headline("some_future_park"));
        Assert.False(AgentParkReasonCopy.TryGet(null, out _));
        Assert.False(AgentParkReasonCopy.TryGet("  ", out _));
    }

    [Fact]
    public void Every_entry_is_keyed_by_its_exact_trimmed_code()
    {
        Assert.NotEmpty(AgentParkReasonCopy.Table);

        foreach (var (code, copy) in AgentParkReasonCopy.Table)
        {
            Assert.Equal(code, code.Trim());
            Assert.All(code, character => Assert.True(
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_',
                $"'{code}' is not a stable park code"));
            Assert.False(string.IsNullOrWhiteSpace(copy.Headline), code);
        }
    }
}
