using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class VerificationEvidenceBudgetTests
{
    [Fact]
    public void Optional_inspection_failures_do_not_exhaust_required_evidence()
    {
        var entries = Enumerable.Range(0, 1024).SelectMany(i => Batch(i * 2 + 1, "optional" + i,
            new { executable = "lookup-" + i, purpose = "inspection" }, false));
        var evidence = VerificationCompletionTests.Snapshot(entries).VerificationEvidence;
        Assert.False(evidence.Overflowed);
        Assert.Null(evidence.BlockingCode);
        Assert.True(evidence.Checks.Count <= RunVerificationEvidence.MaximumChecks);
    }

    [Fact]
    public void Required_failure_survives_optional_diagnostic_eviction()
    {
        var entries = Batch(1, "required", new { executable = "required-test", purpose = "verification" }, false)
            .Concat(Enumerable.Range(0, 1024).SelectMany(i => Batch(i * 2 + 3, "optional" + i,
                new { executable = "lookup-" + i, purpose = "inspection" }, false)));
        var evidence = VerificationCompletionTests.Snapshot(entries).VerificationEvidence;
        Assert.False(evidence.Overflowed);
        Assert.Equal("process_exit_nonzero", evidence.BlockingCode);
        Assert.Single(evidence.Checks, c => c.IsVerification && c.Status == VerificationCheckStatus.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Successful_expected_failures_do_not_overflow_for_repeated_or_distinct_checks(bool distinct)
    {
        var entries = Enumerable.Range(0, 600).SelectMany(i => Batch(i * 2 + 1, "negative" + i,
            new { executable = distinct ? "negative-" + i : "negative", purpose = "expected_failure", expected_exit_code = 1 }, false)).ToArray();
        var snapshot = VerificationCompletionTests.Snapshot(entries);
        Assert.False(snapshot.VerificationEvidence.Overflowed);
        Assert.Null(snapshot.VerificationEvidence.BlockingCode);
        Assert.False(FailedToolCompletionGuard.TryGetFailure(snapshot, out _));
    }

    [Fact]
    public void Expected_exit_mismatch_survives_optional_eviction()
    {
        var entries = Batch(1, "negative", new { executable = "negative", purpose = "expected_failure", expected_exit_code = 7 }, false)
            .Concat(Enumerable.Range(0, 600).SelectMany(i => Batch(i * 2 + 3, "optional" + i,
                new { executable = "lookup-" + i, purpose = "inspection" }, false)));
        var evidence = VerificationCompletionTests.Snapshot(entries).VerificationEvidence;
        Assert.False(evidence.Overflowed);
        Assert.Equal("expected_exit_mismatch", evidence.BlockingCode);
    }

    [Theory]
    [InlineData("read_only", true, false)]
    [InlineData("read_only", false, true)]
    [InlineData("unknown", true, true)]
    [InlineData(null, true, true)]
    public void Only_authority_identified_read_only_commands_preserve_passed_checks(string? effect, bool identity, bool stale)
    {
        var verify = Batch(1, "verify", new { executable = "test", purpose = "verification" }, true);
        var content = JsonSerializer.Serialize(new { exit_code = 0, terminal_effect = effect,
            verification_identity = identity ? new string('a', 64) : null });
        var read = VerificationCompletionTests.Batch(3, "status", "run_process", "{\"executable\":\"git\",\"arguments\":[\"status\"]}",
            ToolExecutionOutcome.Succeeded, "ok", content);
        var evidence = VerificationCompletionTests.Snapshot(verify.Concat(read)).VerificationEvidence;
        Assert.Equal(stale ? "verification_stale" : null, evidence.BlockingCode);
    }

    [Theory]
    [InlineData(-1073741819)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    [InlineData(256)]
    public void Expected_failure_accepts_nonzero_windows_int32_exit_codes(int exitCode)
    {
        var args = JsonSerializer.Serialize(new { executable = "negative", purpose = "expected_failure", expected_exit_code = exitCode });
        var entries = VerificationCompletionTests.Batch(1, "negative", "run_process", args,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", JsonSerializer.Serialize(new { exit_code = exitCode }));
        var snapshot = VerificationCompletionTests.Snapshot(entries);
        Assert.False(FailedToolCompletionGuard.TryGetFailure(snapshot, out _));
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(snapshot.VerificationEvidence.Checks).Status);
    }

    [Theory]
    [InlineData("{\"exit_code\":\"0\"}")]
    [InlineData("{\"exit_code\":0,\"background\":true}")]
    [InlineData("{\"exit_code\":0,\"state\":\"killed\"}")]
    public void Malformed_or_incomplete_read_metadata_cannot_preserve_passed_checks(string output)
    {
        var verify = Batch(1, "verify", new { executable = "test", purpose = "verification" }, true);
        var metadata = System.Text.Json.Nodes.JsonNode.Parse(output)!.AsObject();
        metadata["terminal_effect"] = "read_only";
        metadata["verification_identity"] = new string('a', 64);
        var read = VerificationCompletionTests.Batch(3, "status", "run_process", "{\"executable\":\"git\"}",
            ToolExecutionOutcome.Succeeded, "ok", metadata.ToJsonString());
        Assert.Equal("verification_stale", VerificationCompletionTests.Snapshot(verify.Concat(read)).VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void Failed_default_command_invalidates_checks_even_if_failed_receipt_does_not_claim_effects()
    {
        var entries = Batch(1, "verify", new { executable = "test", purpose = "verification" }, true)
            .Concat(Batch(3, "failed-command", new { executable = "command" }, false));
        Assert.Equal("verification_stale", VerificationCompletionTests.Snapshot(entries).VerificationEvidence.BlockingCode);
    }

    private static ModelContextEntry[] Batch(long sequence, string id, object arguments, bool success) =>
        VerificationCompletionTests.Batch(sequence, id, "run_process", JsonSerializer.Serialize(arguments),
            success ? ToolExecutionOutcome.Succeeded : ToolExecutionOutcome.Failed,
            success ? "ok" : "process_exit_nonzero", success ? "{\"exit_code\":0}" : "{\"exit_code\":1}");
}
