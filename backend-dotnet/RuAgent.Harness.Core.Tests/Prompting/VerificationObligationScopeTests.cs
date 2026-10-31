using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

/// <summary>
/// ADR 0039 obligation scope. A declared check obliges only while its latest result is failed
/// or pending, and only the same identity can retire it. Edits, deletions, supersedes arguments
/// and undeclared commands never create, stale or retire an obligation.
/// </summary>
public sealed class VerificationObligationScopeTests
{
    private const string DeclaredTest = "{\"mode\":\"shell\",\"script\":\"dotnet test\",\"purpose\":\"verification\"}";
    private const string SourceEdit = "{\"path\":\"src/Widget.cs\",\"content\":\"class Widget {}\"}";

    [Fact]
    public void Passed_check_followed_by_a_source_edit_is_not_an_obligation_and_no_failure_hold_applies()
    {
        var context = Snapshot(Call(1, "test", "bash", DeclaredTest, content: "{\"exit_code\":0}")
            .Concat(Call(3, "edit", "write_file", SourceEdit)));

        Assert.Null(context.VerificationEvidence.BlockingCode);
        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(context.VerificationEvidence.Checks).Status);
    }

    [Fact]
    public void Deleting_a_temporary_check_script_after_it_passed_never_creates_an_obligation()
    {
        // The 2026-10-29 trap: a scratch script is written, passes as a declared check, then is deleted.
        var context = Snapshot(Call(1, "write-scratch", "write_file", "{\"path\":\"scratch/probe.py\",\"content\":\"print(1)\"}")
            .Concat(Call(3, "probe", "run_process",
                "{\"executable\":\"python\",\"arguments\":[\"scratch/probe.py\"],\"purpose\":\"verification\"}",
                content: "{\"exit_code\":0}"))
            .Concat(Call(5, "cleanup", "file_delete", "{\"path\":\"scratch/probe.py\"}")));

        Assert.Null(context.VerificationEvidence.BlockingCode);
        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
        Assert.False(context.VerificationEvidence.SourceEditAwaitingVerification);
    }

    [Fact]
    public void A_failed_check_is_not_retired_by_a_different_passing_check_after_an_edit()
    {
        var context = Snapshot(Call(1, "test", "bash", DeclaredTest, ToolExecutionOutcome.Failed,
                "process_exit_nonzero", "{\"exit_code\":1}")
            .Concat(Call(3, "edit", "write_file", SourceEdit))
            .Concat(Call(5, "lint", "bash", DeclaredTest.Replace("dotnet test", "dotnet format"),
                content: "{\"exit_code\":0}")));

        Assert.Equal("process_exit_nonzero", context.VerificationEvidence.BlockingCode);
        Assert.True(FailedToolCompletionGuard.TryGetFailure(context, out var code));
        Assert.Equal("process_exit_nonzero", code);
    }

    [Fact]
    public void The_same_failed_check_passing_after_an_edit_resolves_its_obligation()
    {
        var context = Snapshot(Call(1, "test", "bash", DeclaredTest, ToolExecutionOutcome.Failed,
                "process_exit_nonzero", "{\"exit_code\":1}")
            .Concat(Call(3, "edit", "write_file", SourceEdit))
            .Concat(Call(5, "retest", "bash", DeclaredTest, content: "{\"exit_code\":0}")));

        Assert.Null(context.VerificationEvidence.BlockingCode);
        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Theory]
    [InlineData("[\"probe\"]")]
    [InlineData("[\"unknown-id\"]")]
    [InlineData("7")]
    [InlineData("[]")]
    public void Supersedes_is_accepted_and_ignored_so_a_failed_check_stays_outstanding(string supersedes)
    {
        var context = Snapshot(Call(1, "probe", "bash", DeclaredTest, ToolExecutionOutcome.Failed,
                "process_exit_nonzero", "{\"exit_code\":1}")
            .Concat(Call(3, "edit", "write_file", SourceEdit))
            .Concat(Call(5, "final", "run_process",
                "{\"executable\":\"python\",\"arguments\":[\"smoke.py\"],\"purpose\":\"verification\",\"supersedes\":" + supersedes + "}",
                content: "{\"exit_code\":0}")));

        Assert.Equal("process_exit_nonzero", context.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void A_pending_check_whose_execution_overlapped_an_edit_resolves_only_on_its_own_exit()
    {
        var edited = Call(1, "start", "bash", DeclaredTest, content: "{\"execution_id\":\"bg-1\",\"state\":\"running\"}")
            .Concat(Call(3, "edit", "write_file", SourceEdit))
            .ToArray();
        Assert.Equal("verification_pending", Snapshot(edited).VerificationEvidence.BlockingCode);

        var exited = edited.Concat(Call(5, "poll", "bash", "{\"mode\":\"read_output\",\"execution_id\":\"bg-1\"}",
            content: "{\"state\":\"exited\",\"exit_code\":0}"));
        Assert.Null(Snapshot(exited).VerificationEvidence.BlockingCode);
    }

    [Theory]
    [InlineData(ToolExecutionOutcome.Failed, "process_exit_nonzero", "{\"exit_code\":128}")]
    [InlineData(ToolExecutionOutcome.Failed, "process_timed_out", "{\"exit_code\":1}")]
    [InlineData(ToolExecutionOutcome.OutcomeUnknown, "process_exit_nonzero", "{\"exit_code\":1}")]
    [InlineData(ToolExecutionOutcome.Succeeded, "ok", "{\"exit_code\":1}")]
    public void Undeclared_commands_never_change_a_passed_declared_check(
        ToolExecutionOutcome outcome, string code, string content)
    {
        var context = Snapshot(Call(1, "test", "bash", DeclaredTest, content: "{\"exit_code\":0}")
            .Concat(Call(3, "undeclared", "bash", "{\"mode\":\"shell\",\"script\":\"git status\"}", outcome, code, content)));

        Assert.Null(context.VerificationEvidence.BlockingCode);
        Assert.False(context.VerificationEvidence.HasSourceEdits);
        Assert.Equal(VerificationCheckStatus.Passed,
            Assert.Single(context.VerificationEvidence.Checks, check => check.IsVerification).Status);
    }

    [Theory]
    [InlineData(128, null, false)]
    [InlineData(128, "exited", true)]
    [InlineData(-1073741819, "exited", false)]
    public void Failed_undeclared_lookup_keeps_a_diagnostic_and_preserves_the_passed_check(
        int exitCode, string? state, bool background)
    {
        var evidence = RunVerificationEvidence.Project(PassedCheck().Concat(Lookup(3, exitCode, state, background)));

        Assert.Null(evidence.BlockingCode);
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(evidence.Checks, check => check.IsVerification).Status);
        var diagnostic = Assert.Single(evidence.Checks, check => !check.IsVerification);
        Assert.Equal(VerificationCheckStatus.Failed, diagnostic.Status);
        Assert.Equal("process_exit_nonzero", diagnostic.Code);
        Assert.Equal(exitCode, diagnostic.ObservedExitCode);
    }

    [Fact]
    public void An_undeclared_failure_does_not_invalidate_a_running_verification()
    {
        var pending = VerificationCompletionTests.Batch(1, "verify", "run_process",
            "{\"executable\":\"test\",\"purpose\":\"verification\",\"background\":true}",
            ToolExecutionOutcome.Succeeded, "ok", "{\"execution_id\":\"background-check\",\"state\":\"running\"}");
        var entries = pending.Concat(Lookup(3, 128)).Concat(VerificationCompletionTests.Batch(5, "poll", "terminal_read",
            "{\"execution_id\":\"background-check\"}", ToolExecutionOutcome.Succeeded, "ok",
            "{\"execution_id\":\"background-check\",\"state\":\"exited\",\"exit_code\":0}"));

        var evidence = RunVerificationEvidence.Project(entries);

        Assert.Null(evidence.BlockingCode);
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(evidence.Checks, check => check.IsVerification).Status);
    }

    [Theory]
    [InlineData("{\"state\":123}")]
    [InlineData("{\"state\":\"running\"}")]
    [InlineData("{\"background\":\"true\"}")]
    [InlineData("{\"background\":true}")]
    public void Undeclared_read_only_metadata_never_changes_a_passed_check(string metadata)
    {
        var output = System.Text.Json.Nodes.JsonNode.Parse(metadata)!.AsObject();
        output["exit_code"] = 128;
        output["terminal_effect"] = "read_only";
        output["verification_identity"] = new string('B', 64);
        var read = VerificationCompletionTests.Batch(3, "read", "run_process",
            "{\"executable\":\"git\",\"arguments\":[\"status\"]}", ToolExecutionOutcome.Failed,
            "process_exit_nonzero", output.ToJsonString());

        var evidence = RunVerificationEvidence.Project(PassedCheck().Concat(read));

        Assert.Null(evidence.BlockingCode);
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(evidence.Checks, check => check.IsVerification).Status);
    }

    [Fact]
    public void Declared_check_rejected_before_launch_creates_no_check_and_no_obligation()
    {
        var rejected = Snapshot(Call(1, "malformed", "run_process", "{\"executable\":\"python\",\"purpose\":\"verification\"}",
            ToolExecutionOutcome.Failed, "malformed_call", "{}"));

        Assert.Empty(rejected.VerificationEvidence.Checks);
        Assert.Null(rejected.VerificationEvidence.BlockingCode);
        Assert.Null(rejected.VerificationEvidence.DescribePending());
        // The failed receipt itself is still the trailing failure for the tool-failure rule.
        Assert.True(FailedToolCompletionGuard.TryGetFailure(rejected, out var code));
        Assert.Equal("malformed_call", code);
    }

    [Theory]
    [InlineData("malformed_call")]
    [InlineData("invalid_arguments")]
    public void Rejected_declared_check_after_a_pass_does_not_obligate_the_run(string code)
    {
        var context = Snapshot(Call(1, "test", "bash", DeclaredTest, content: "{\"exit_code\":0}")
            .Concat(Call(3, "malformed", "run_process", "{\"executable\":\"python\",\"purpose\":\"verification\"}",
                ToolExecutionOutcome.Failed, code, "{}")));

        Assert.Null(context.VerificationEvidence.BlockingCode);
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(context.VerificationEvidence.Checks).Status);
    }

    [Fact]
    public void A_rejected_declared_check_never_blocks_a_later_passing_negative_check()
    {
        var context = Snapshot(Call(1, "malformed", "run_process", "{\"executable\":\"python\",\"purpose\":\"verification\"}",
                ToolExecutionOutcome.Failed, "malformed_call", "{}")
            .Concat(Call(3, "negative", "bash",
                "{\"mode\":\"shell\",\"script\":\"exit 1\",\"purpose\":\"expected_failure\",\"expected_exit_code\":1}",
                ToolExecutionOutcome.Failed, "process_exit_nonzero", "{\"exit_code\":1}")));

        Assert.Null(context.VerificationEvidence.BlockingCode);
        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Fact]
    public void Canonical_metadata_identity_takes_precedence_and_otherwise_the_raw_command_identifies_the_check()
    {
        var canonical = Snapshot(Call(1, "canonical", "bash", DeclaredTest,
                content: JsonSerializer.Serialize(new { exit_code = 0, verification_identity = new string('D', 64) })))
            .VerificationEvidence.Checks.Single().Identity;
        var raw = Snapshot(Call(1, "raw", "bash", DeclaredTest, content: "{\"exit_code\":0}"))
            .VerificationEvidence.Checks.Single().Identity;

        Assert.Equal("canonical:" + new string('D', 64) + ":0", canonical);
        Assert.False(raw.StartsWith("canonical:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_raw_identity_failure_is_not_retired_by_a_canonical_pass_of_the_same_command()
    {
        var context = Snapshot(Call(1, "legacy", "bash", DeclaredTest, ToolExecutionOutcome.Failed,
                "process_exit_nonzero", "{\"exit_code\":1}")
            .Concat(Call(3, "canonical", "bash", DeclaredTest, content: JsonSerializer.Serialize(
                new { exit_code = 0, verification_identity = new string('C', 64) }))));

        Assert.Equal("process_exit_nonzero", context.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void An_invalid_inline_probe_stays_outstanding_after_a_corrected_probe_with_other_arguments()
    {
        // Receipts written before the repair annotations were removed may still carry verification_repair_identity
        // and verification_invocation_invalid. The reader ignores them, and no field retires a failed check: a
        // corrected probe with other arguments does not clear it (ADR 0039 item 8).
        var invalid = Call(1, "bad-inline", "run_process",
            "{\"executable\":\"python\",\"arguments\":[\"-c\",\"from a b import value\"],\"purpose\":\"verification\",\"working_directory\":\"C:/workspace\"}",
            ToolExecutionOutcome.Failed, "process_exit_nonzero", JsonSerializer.Serialize(new
            {
                exit_code = 1, verification_identity = new string('A', 64), verification_repair_identity = new string('B', 64),
                verification_invocation_invalid = true,
            }));
        var corrected = Call(3, "corrected-inline", "run_process",
            "{\"executable\":\"python\",\"arguments\":[\"-c\",\"import importlib; print('verified')\"],\"purpose\":\"verification\",\"working_directory\":\"C:/workspace\"}",
            content: JsonSerializer.Serialize(new
            {
                exit_code = 0, verification_identity = new string('C', 64), verification_repair_identity = new string('B', 64),
            }));

        var context = Snapshot(invalid.Concat(corrected));

        Assert.Equal("process_exit_nonzero", context.VerificationEvidence.BlockingCode);
        Assert.Equal(VerificationCheckStatus.Failed,
            Assert.Single(context.VerificationEvidence.Checks, check => check.ProviderCallId == "bad-inline").Status);
    }

    [Fact]
    public void An_expected_exit_mismatch_stays_outstanding_after_a_later_different_passing_check()
    {
        var context = Snapshot(Call(1, "negative", "run_process",
                "{\"executable\":\"python\",\"arguments\":[\"negative.py\"],\"purpose\":\"expected_failure\",\"expected_exit_code\":7}",
                ToolExecutionOutcome.Failed, "process_exit_nonzero", "{\"exit_code\":1}")
            .Concat(Call(3, "edit", "write_file", SourceEdit))
            .Concat(Call(5, "smoke", "run_process", "{\"executable\":\"python\",\"arguments\":[\"smoke.py\"],\"purpose\":\"verification\"}",
                content: "{\"exit_code\":0}")));

        Assert.Equal("expected_exit_mismatch", context.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void Two_failed_checks_with_reused_provider_call_ids_stay_outstanding_after_a_passing_check()
    {
        var context = Snapshot(Call(1, "reused", "run_process",
                "{\"executable\":\"python\",\"arguments\":[\"a.py\"],\"purpose\":\"verification\"}",
                ToolExecutionOutcome.Failed, "process_exit_nonzero", "{\"exit_code\":1}")
            .Concat(Call(3, "reused", "run_process",
                "{\"executable\":\"python\",\"arguments\":[\"b.py\"],\"purpose\":\"verification\"}",
                ToolExecutionOutcome.Failed, "process_exit_nonzero", "{\"exit_code\":1}"))
            .Concat(Call(5, "final", "run_process",
                "{\"executable\":\"python\",\"arguments\":[\"c.py\"],\"purpose\":\"verification\"}",
                content: "{\"exit_code\":0}")));

        Assert.Equal(2, context.VerificationEvidence.Checks.Count(check => check.Status == VerificationCheckStatus.Failed));
        Assert.Equal("process_exit_nonzero", context.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void Incremental_projection_matches_full_replay_across_a_source_edit_and_a_new_check()
    {
        var before = Call(1, "test", "bash", DeclaredTest, content: "{\"exit_code\":0}")
            .Concat(Call(3, "edit", "write_file", SourceEdit))
            .ToArray();
        var after = Call(5, "retest", "bash", DeclaredTest, ToolExecutionOutcome.Failed, "process_exit_nonzero",
            "{\"exit_code\":1}").ToArray();

        var incremental = RunVerificationEvidence.Project(after, RunVerificationEvidence.Project(before));
        var full = RunVerificationEvidence.Project(before.Concat(after));

        Assert.Equal("process_exit_nonzero", full.BlockingCode);
        Assert.Equal(full.BlockingCode, incremental.BlockingCode);
        Assert.Equal(full.Checks, incremental.Checks);
        Assert.Equal(full.HasSourceEdits, incremental.HasSourceEdits);
        Assert.Equal(full.SourceEditAwaitingVerification, incremental.SourceEditAwaitingVerification);
    }

    [Fact]
    public void Passed_check_without_any_journal_or_timestamp_input_stays_passed()
    {
        var evidence = RunVerificationEvidence.Project(Call(1, "test", "bash", DeclaredTest, content: "{\"exit_code\":0}"));

        Assert.Null(evidence.BlockingCode);
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(evidence.Checks).Status);
    }

    private static ModelContextSnapshot Snapshot(IEnumerable<ModelContextEntry> entries) =>
        VerificationCompletionTests.Snapshot(entries);

    private static ModelContextEntry[] Call(long sequence, string id, string tool, string arguments,
        ToolExecutionOutcome outcome = ToolExecutionOutcome.Succeeded, string code = "ok", string content = "{}") =>
        VerificationCompletionTests.Batch(sequence, id, tool, arguments, outcome, code, content);

    private static ModelContextEntry[] PassedCheck() =>
        Call(1, "verify", "run_process", "{\"executable\":\"test\",\"purpose\":\"verification\"}", content: "{\"exit_code\":0}");

    private static ModelContextEntry[] Lookup(long sequence, int exitCode, string? state = null, bool background = false) =>
        VerificationCompletionTests.Batch(sequence, "lookup", "run_process",
            JsonSerializer.Serialize(new { executable = "git", arguments = new[] { "status" } }),
            ToolExecutionOutcome.Failed, "process_exit_nonzero",
            JsonSerializer.Serialize(new { exit_code = exitCode, state, background }));
}
