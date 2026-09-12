using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class TerminalFailureCardPolicyTests
{
    [Theory]
    [InlineData("failed", false, false, true)]
    [InlineData("interrupted", false, false, true)]
    [InlineData("failed", true, false, false)]
    [InlineData("failed", false, true, false)]
    [InlineData("completed", false, false, false)]
    [InlineData("cancelled", false, false, false)]
    [InlineData(null, false, false, false)]
    public void Append_only_for_unreplied_failures_without_an_existing_card(
        string? runStatus,
        bool hasRenderedReply,
        bool hasExistingErrorCard,
        bool expected) =>
        Assert.Equal(
            expected,
            TerminalFailureCardPolicy.ShouldAppend(runStatus, hasRenderedReply, hasExistingErrorCard));

    [Fact]
    public void Composes_actionable_copy_with_continue_guidance()
    {
        string content = TerminalFailureCardPolicy.Compose("repeated_tool_call", canContinue: true);

        Assert.StartsWith("**", content, StringComparison.Ordinal);
        Assert.Contains("repeated the same action", content, StringComparison.Ordinal);
        Assert.Contains("Type **Continue** to resume.", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Omits_continue_guidance_when_resume_is_not_available()
    {
        string content = TerminalFailureCardPolicy.Compose("provider_rate_limited", canContinue: false);

        Assert.Contains("rate-limited", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Type **Continue** to resume.", content, StringComparison.Ordinal);
    }

    [Fact]
    public void Falls_back_to_the_plain_failure_copy_without_a_reason()
    {
        Assert.Equal("**Coding Agent failed**", TerminalFailureCardPolicy.Compose(null, canContinue: false));
    }
}
