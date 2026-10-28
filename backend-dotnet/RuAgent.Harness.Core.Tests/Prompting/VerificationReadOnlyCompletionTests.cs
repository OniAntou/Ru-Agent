using System.Text.Json;
using System.Text.Json.Nodes;
using RuAgent.Harness.Core.Context;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class VerificationReadOnlyCompletionTests
{
    [Theory]
    [InlineData("{\"state\":123}")]
    [InlineData("{\"state\":{}}")]
    [InlineData("{\"state\":\"running\"}")]
    [InlineData("{\"background\":\"true\"}")]
    [InlineData("{\"background\":1}")]
    [InlineData("{\"background\":true}")]
    public void Malformed_or_running_read_metadata_cannot_preserve_checks(string metadata)
    {
        var output = JsonNode.Parse(metadata)!.AsObject();
        output["exit_code"] = 128;
        output["terminal_effect"] = "read_only";
        output["verification_identity"] = new string('b', 64);
        var read = VerificationCompletionTests.Batch(3, "read", "run_process",
            """{"executable":"git","arguments":["status"]}""", ToolExecutionOutcome.Failed, "process_exit_nonzero", output.ToJsonString());

        var evidence = RunVerificationEvidence.Project(PassedCheck().Concat(read));

        Assert.Equal("verification_stale", evidence.BlockingCode);
    }

    [Theory]
    [InlineData(128, null, false)]
    [InlineData(128, "exited", true)]
    [InlineData(-1073741819, "exited", false)]
    public void Failed_read_only_completion_preserves_other_checks_and_its_failed_diagnostic(
        int exitCode, string? state, bool background)
    {
        var entries = PassedCheck().Concat(Read(3, exitCode, state: state, background: background));
        var evidence = RunVerificationEvidence.Project(entries);

        Assert.False(evidence.HasMutations);
        Assert.Null(evidence.BlockingCode);
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(evidence.Checks, check => check.IsVerification).Status);
        var diagnostic = Assert.Single(evidence.Checks, check => !check.IsVerification);
        Assert.Equal(VerificationCheckStatus.Failed, diagnostic.Status);
        Assert.Equal("process_exit_nonzero", diagnostic.Code);
        Assert.Equal(exitCode, diagnostic.ObservedExitCode);
    }

    [Fact]
    public void Required_read_only_failure_remains_outstanding_without_staling_another_check()
    {
        var evidence = RunVerificationEvidence.Project(PassedCheck().Concat(Read(3, 128, purpose: "verification")));

        Assert.False(evidence.HasMutations);
        Assert.Equal("process_exit_nonzero", evidence.BlockingCode);
        Assert.Contains(evidence.Checks, check => check.ProviderCallId == "verify" && check.Status == VerificationCheckStatus.Passed);
        Assert.Contains(evidence.Checks, check => check.ProviderCallId == "read" && check.IsVerification
            && check.Status == VerificationCheckStatus.Failed && check.ObservedExitCode == 128);
    }

    [Fact]
    public void Read_only_failure_does_not_invalidate_a_running_verification()
    {
        var pending = VerificationCompletionTests.Batch(1, "verify", "run_process",
            """{"executable":"test","purpose":"verification","background":true}""",
            ToolExecutionOutcome.Succeeded, "ok", """{"execution_id":"background-check","state":"running"}""");
        var entries = pending.Concat(Read(3, 128)).Concat(VerificationCompletionTests.Batch(5, "poll", "terminal_read",
            """{"execution_id":"background-check"}""", ToolExecutionOutcome.Succeeded, "ok",
            """{"execution_id":"background-check","state":"exited","exit_code":0}"""));

        var evidence = RunVerificationEvidence.Project(entries);

        Assert.Null(evidence.BlockingCode);
        Assert.False(Assert.Single(evidence.Checks, check => check.IsVerification).InvalidatedDuringExecution);
    }

    [Theory]
    [InlineData(false, "not_applicable")]
    [InlineData(true, "unverified")]
    public void Summary_distinguishes_a_failed_lookup_from_a_required_read_only_check(bool required, string expected)
    {
        var evidence = RunVerificationEvidence.Project(Read(1, 128, purpose: required ? "verification" : null));
        var timestamp = DateTimeOffset.UnixEpoch;
        var run = new AgentRun(new RunId("run_" + new string('1', 32)), new SessionId(new string('1', 32)),
            new WorkspaceId("ws_" + new string('1', 32)), new HarnessRunStatus(HarnessRunState.Completed),
            1, 1, 1, 0, 0, AgentPermissionPreset.Standard, 1, new string('a', 64), timestamp, timestamp, timestamp, null);

        var summary = AgentRunSummary.FromTerminalRun(run, "Workspace", verificationEvidence: evidence);

        Assert.Equal(expected, summary.VerificationState);
    }

    [Theory]
    [InlineData(ToolExecutionOutcome.Succeeded, "ok", 128)]
    [InlineData(ToolExecutionOutcome.Failed, "process_exit_nonzero", 0)]
    [InlineData(ToolExecutionOutcome.Failed, "process_timed_out", 128)]
    [InlineData(ToolExecutionOutcome.TimedOut, "process_timed_out", 128)]
    [InlineData(ToolExecutionOutcome.Cancelled, "process_cancelled", 128)]
    [InlineData(ToolExecutionOutcome.OutcomeUnknown, "process_exit_nonzero", 128)]
    public void Read_only_metadata_does_not_hide_inconsistent_or_uncertain_completion(
        ToolExecutionOutcome outcome, string code, int exitCode)
    {
        var content = JsonSerializer.Serialize(new { exit_code = exitCode, terminal_effect = "read_only",
            verification_identity = new string('b', 64), state = "exited" });
        var read = VerificationCompletionTests.Batch(3, "read", "run_process",
            """{"executable":"git","arguments":["status"]}""", outcome, code, content);

        var evidence = RunVerificationEvidence.Project(PassedCheck().Concat(read));

        Assert.True(evidence.HasMutations);
        Assert.Equal("verification_stale", evidence.BlockingCode);
    }

    private static ModelContextEntry[] PassedCheck() => VerificationCompletionTests.Batch(1, "verify", "run_process",
        """{"executable":"test","purpose":"verification"}""", ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""");

    private static ModelContextEntry[] Read(long sequence, int exitCode, string? purpose = null,
        string? state = null, bool background = false) => VerificationCompletionTests.Batch(sequence, "read", "run_process",
        JsonSerializer.Serialize(new { executable = "git", arguments = new[] { "status" }, purpose }),
        ToolExecutionOutcome.Failed, "process_exit_nonzero", JsonSerializer.Serialize(new
        { exit_code = exitCode, terminal_effect = "read_only", verification_identity = new string('b', 64), state, background }));
}
