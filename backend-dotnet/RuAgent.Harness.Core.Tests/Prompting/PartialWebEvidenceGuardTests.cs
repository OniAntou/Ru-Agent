using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class PartialWebEvidenceGuardTests
{
    [Fact]
    public void Readable_page_can_support_a_reply_when_another_page_is_empty()
    {
        var context = Context("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_content_empty", false, true, true);
        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Theory]
    [InlineData("web_http_401")]
    [InlineData("web_http_403")]
    [InlineData("web_http_404")]
    [InlineData("web_http_410")]
    [InlineData("web_http_429")]
    [InlineData("web_http_503")]
    [InlineData("web_network_error")]
    [InlineData("web_page_blocked")]
    [InlineData("web_content_type_not_supported")]
    [InlineData("web_pdf_unreadable")]
    [InlineData("web_pdf_encrypted")]
    [InlineData("web_response_too_large")]
    [InlineData("web_redirect_limit")]
    [InlineData("web_redirect_invalid")]
    [InlineData("web_encoding_invalid")]
    public void Readable_page_can_support_a_reply_when_another_fetch_has_an_ordinary_failure(string failureCode)
    {
        var context = Context("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, failureCode, false, true, true);

        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
        Assert.Equal(ToolExecutionOutcome.Failed, context.Entries[^1].ToolReceipt!.Outcome);
        Assert.Equal(failureCode, context.Entries[^1].ToolReceipt!.Code);
    }

    [Fact]
    public void Optional_fetch_failure_does_not_clear_a_required_verification_failure()
    {
        var verificationFailure = VerificationCompletionTests.Batch(1, "required-check", "bash",
            "{\"script\":\"dotnet test\",\"purpose\":\"verification\"}",
            ToolExecutionOutcome.Failed, "process_exit_nonzero", "{\"exit_code\":1}");
        var optionalFetch = Context("web_fetch", "1.0.0", ToolExecutionOutcome.Failed,
            "web_http_404", false, true, true, sequence: 3).Entries;
        var context = VerificationCompletionTests.Snapshot(verificationFailure.Concat(optionalFetch));

        Assert.Equal("process_exit_nonzero", context.VerificationEvidence.BlockingCode);
        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("process_exit_nonzero", code);
    }

    [Theory]
    [InlineData("write", "1.0.0", ToolExecutionOutcome.Failed, "web_content_empty", false, true, true)]
    [InlineData("web_fetch", "2.0.0", ToolExecutionOutcome.Failed, "web_content_empty", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "rule_denied", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "approval_denied", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_uri_not_allowed", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_private_host", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_offset_invalid", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.OutcomeUnknown, "web_content_empty", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Cancelled, "web_content_empty", false, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_content_empty", true, true, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_content_empty", false, false, true)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_content_empty", false, true, false)]
    [InlineData("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "web_http_404", false, true, false)]
    public void Missing_evidence_binding_or_other_failures_remain_guarded(string tool, string version,
        ToolExecutionOutcome outcome, string code, bool effects, bool matchingProvider, bool hasSuccess)
    {
        Assert.True(FailedToolCompletionGuard.TryGetFailure(
            Context(tool, version, outcome, code, effects, matchingProvider, hasSuccess), out _));
    }

    [Fact]
    public void Partial_web_sources_do_not_bypass_a_repeated_policy_deny()
    {
        var first = Context("web_fetch", "1.0.0", ToolExecutionOutcome.Failed, "policy_denied", false, true, true);
        // Preserve the existing single-denial repair opportunity.
        Assert.False(FailedToolCompletionGuard.TryGetFailure(first, out _));
        var repeated = new ModelContextSnapshot(first.Entries.Concat(first.Entries).ToArray(), "[]"u8.ToArray());
        Assert.True(FailedToolCompletionGuard.TryGetFailure(repeated, out var code));
        Assert.Equal("policy_denied", code);
    }

    internal static ModelContextSnapshot Context(string secondTool, string secondVersion, ToolExecutionOutcome secondOutcome,
        string secondCode, bool effects, bool matchingProvider, bool hasSuccess, long sequence = 1)
    {
        using var args = JsonDocument.Parse("{\"url\":\"https://example.invalid/fixture\"}");
        ModelToolCall[] calls = [new("source_one", "web_fetch", "1.0.0", args.RootElement),
            new("source_two", secondTool, secondVersion, args.RootElement)];
        ToolExecutionReceipt Receipt(int id, ToolExecutionOutcome outcome, string code, bool sideEffects, string provider) =>
            new(new("exec_" + id.ToString("x32")), new("call_" + id.ToString("x32")), outcome, code, null, sideEffects,
                providerCallId: provider);
        ModelContextEntry[] entries = [
            ModelContextEntry.Assistant("sources", sequence, new(null, calls, AssistantFinishReason.ToolCalls, 1)),
            ModelContextEntry.ToolResult("source-one", sequence + 1,
                Receipt(1, hasSuccess ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
                    hasSuccess ? "ok" : "web_content_empty", false, "source_one"), "{\"text\":\"source evidence\"}"),
            ModelContextEntry.ToolResult("source-two", sequence + 2,
                Receipt(2, secondOutcome, secondCode, effects, matchingProvider ? "source_two" : "foreign_call"), "{}"),
        ];
        return new(entries, "[]"u8.ToArray());
    }
}
