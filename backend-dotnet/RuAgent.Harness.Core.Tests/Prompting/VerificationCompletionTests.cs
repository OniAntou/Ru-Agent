using System.Text.Json;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class VerificationCompletionTests
{
    [Fact]
    public void Passed_check_is_not_invalidated_by_a_later_edit_and_a_rerun_passes_too()
    {
        const string args = """{"script":"dotnet test","purpose":"verification"}""";
        var initial = Batch(1, "check", "powershell", args, ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""")
            .Concat(Batch(3, "change", "edit_file", "{}", ToolExecutionOutcome.Succeeded, "ok", "{}"));
        Assert.Equal(VerificationCheckStatus.Passed, Assert.Single(Snapshot(initial).VerificationEvidence.Checks).Status);
        Assert.Null(Snapshot(initial).VerificationEvidence.BlockingCode);
        var repaired = initial.Concat(Batch(5, "recheck", "powershell", args, ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""));
        Assert.False(FailedToolCompletionGuard.TryGetFailure(Snapshot(repaired), out _));
    }

    [Fact]
    public void Expected_nonzero_exit_can_complete_without_rewriting_failed_receipt()
    {
        var entries = Batch(1, "negative", "bash",
            """{"mode":"shell","script":"exit 1","purpose":"expected_failure","expected_exit_code":1}""",
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""");
        Assert.False(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out _));
        Assert.Equal(ToolExecutionOutcome.Failed, entries[^1].ToolReceipt!.Outcome);
    }

    [Fact]
    public void Reading_a_log_does_not_resolve_failed_verification()
    {
        var entries = Batch(1, "test", "bash", Verification,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""")
            .Concat(Batch(3, "read", "read", "{}", ToolExecutionOutcome.Succeeded, "ok", "{}"));
        Assert.True(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out var code));
        Assert.Equal("process_exit_nonzero", code);
    }

    [Fact]
    public void A_later_code_change_does_not_turn_a_passed_verification_into_a_failure()
    {
        var entries = Batch(1, "test", "bash", Verification,
            ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""")
            .Concat(Batch(3, "edit", "edit", "{}", ToolExecutionOutcome.Succeeded, "ok", "{}"));
        Assert.False(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out _));
        Assert.Null(Snapshot(entries).VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void A_different_passing_check_does_not_resolve_a_failed_check()
    {
        var entries = Batch(1, "test", "bash", Verification,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""")
            .Concat(Batch(3, "lint", "bash", Verification.Replace("dotnet test", "dotnet format"),
                ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""));
        Assert.True(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out _));
    }

    [Fact]
    public void Rerunning_the_same_check_after_edits_resolves_its_failure()
    {
        var entries = Batch(1, "test", "bash", Verification,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""")
            .Concat(Batch(3, "edit", "edit", "{}", ToolExecutionOutcome.Succeeded, "ok", "{}"))
            .Concat(Batch(5, "retest", "bash", Verification,
                ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""));
        Assert.False(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out _));
    }

    [Fact]
    public void A_background_verification_is_pending_until_its_exit_is_observed()
    {
        var entries = Batch(1, "test", "bash", Verification,
            ToolExecutionOutcome.Succeeded, "ok", """{"execution_id":"bg-1","state":"running"}""");
        Assert.True(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out var code));
        Assert.Equal("verification_pending", code);
        var completed = entries.Concat(Batch(3, "output", "bash",
            """{"mode":"read_output","execution_id":"bg-1"}""", ToolExecutionOutcome.Succeeded,
            "ok", """{"state":"exited","exit_code":0}"""));
        Assert.False(FailedToolCompletionGuard.TryGetFailure(Snapshot(completed), out _));
    }

    [Theory]
    [InlineData("policy_denied", "{\"exit_code\":1}", ToolExecutionOutcome.OutcomeUnknown)]
    [InlineData("process_timed_out", "{\"exit_code\":1}", ToolExecutionOutcome.Failed)]
    [InlineData("process_exit_nonzero", "{\"exit_code\":2}", ToolExecutionOutcome.Failed)]
    public void Expected_exit_does_not_accept_denials_unknown_outcomes_timeouts_or_a_different_code(
        string code, string content, ToolExecutionOutcome outcome)
    {
        var entries = Batch(1, "negative", "bash",
            """{"mode":"shell","script":"exit 1","purpose":"expected_failure","expected_exit_code":1}""",
            outcome, code, content);
        Assert.True(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out _));
    }

    [Fact]
    public void Session_history_cannot_inherit_outstanding_checks_into_a_new_run()
    {
        var oldRun = Batch(1, "old-test", "bash", Verification,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""");
        var source = new ModelContextSourceSnapshot(oldRun, []);
        Assert.Null(source.VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void Distinct_check_overflow_fails_closed_instead_of_dropping_old_failures()
    {
        var entries = Enumerable.Range(0, RunVerificationEvidence.MaximumChecks + 1)
            .SelectMany(index => Batch(index * 2 + 1, "test-" + index, "bash",
                Verification.Replace("dotnet test", "dotnet test project" + index),
                ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""));
        var evidence = Snapshot(entries).VerificationEvidence;
        Assert.Equal(RunVerificationEvidence.MaximumChecks, evidence.Checks.Count);
        Assert.Equal("verification_tracking_limit", evidence.BlockingCode);
    }

    [Fact]
    public void Explicit_default_mode_and_workspace_cwd_resolve_the_same_unannotated_failure()
    {
        var entries = Batch(1, "test", "bash", """{"executable":"test.exe"}""",
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""")
            .Concat(Batch(3, "retest", "bash",
                """{"executable":"test.exe","mode":"run","working_directory":".","purpose":"verification"}""",
                ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""));
        Assert.False(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out _));
    }

    [Fact]
    public void A_failed_receipt_with_inconsistent_zero_exit_cannot_be_cleared_by_reading_a_file()
    {
        var entries = Batch(1, "test", "bash", Verification,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":0}""")
            .Concat(Batch(3, "read", "read", "{}", ToolExecutionOutcome.Succeeded, "ok", "{}"));
        Assert.True(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out var code));
        Assert.Equal("verification_result_missing", code);
    }

    [Fact]
    public void Bounded_command_and_rejected_checkpoint_excerpts_preserve_unicode_pairs()
    {
        var args = JsonSerializer.Serialize(new { mode = "shell", script = new string('a', 511) + "\U0001F642tail",
            purpose = "verification" });
        var snapshot = Snapshot(Batch(1, "unicode", "bash", args,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}"""));
        var reference = new PromptReference("verification_state", "checks", snapshot.VerificationEvidence.DescribePending()!);
        Assert.Contains("command excerpt", reference.Content);
        var repaired = ContextCheckpoint.WithRepairDirective(snapshot, new string('a', 3999) + "\U0001F642tail");
        var excerpt = repaired.Entries[^1].Content!;
        Assert.Equal(excerpt, excerpt.Normalize());
    }

    [Fact]
    public void Background_check_started_before_a_mutation_is_not_an_obligation()
    {
        var entries = Batch(1, "start", "bash", Verification, ToolExecutionOutcome.Succeeded,
            "ok", """{"execution_id":"background-test","state":"running"}""")
            .Concat(Batch(3, "edit", "edit", "{}", ToolExecutionOutcome.Succeeded, "ok", "{}"))
            .Concat(Batch(5, "finish", "bash", """{"mode":"read_output","execution_id":"background-test"}""",
                ToolExecutionOutcome.Succeeded, "ok", """{"state":"exited","exit_code":0}"""));
        Assert.False(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out _));
        Assert.Null(Snapshot(entries).VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void Authority_identity_resolves_cwd_aliases_and_preserves_distinct_commands()
    {
        var digest = new string('a', 64);
        var failed = Batch(1, "test", "bash", Verification, ToolExecutionOutcome.Failed,
            "process_exit_nonzero", JsonSerializer.Serialize(new { exit_code = 1, verification_identity = digest }));
        var alias = Verification.Replace("\"purpose\"", "\"working_directory\":\"C:/workspace\",\"purpose\"");
        var passed = Batch(3, "retest", "bash", alias, ToolExecutionOutcome.Succeeded,
            "ok", JsonSerializer.Serialize(new { exit_code = 0, verification_identity = digest }));
        Assert.False(FailedToolCompletionGuard.TryGetFailure(Snapshot(failed.Concat(passed)), out _));
        var distinct = Batch(3, "different", "bash", alias, ToolExecutionOutcome.Succeeded,
            "ok", JsonSerializer.Serialize(new { exit_code = 0, verification_identity = new string('b', 64) }));
        Assert.True(FailedToolCompletionGuard.TryGetFailure(Snapshot(failed.Concat(distinct)), out _));
    }

    [Fact]
    public void A_check_rejected_before_launch_creates_no_obligation_and_a_corrected_check_passes()
    {
        var rejected = Batch(1, "malformed", "run_process", """{"executable":"python","purpose":"verification"}""",
            ToolExecutionOutcome.Failed, "malformed_call", "{}");
        Assert.True(FailedToolCompletionGuard.TryGetFailure(Snapshot(rejected), out var code));
        Assert.Equal("malformed_call", code);
        Assert.Null(Snapshot(rejected).VerificationEvidence.DescribePending());
        Assert.Contains("keep that purpose", AgentRecoveryNotes.BuildUnverifiedNote(code));

        var corrected = rejected
            .Concat(Batch(3, "write", "write_file", "{}", ToolExecutionOutcome.Succeeded, "ok", "{}"))
            .Concat(Batch(5, "run", "powershell", """{"script":"python gen_docs.py","purpose":"verification"}""",
                ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""))
            .Concat(Batch(7, "list", "list", "{}", ToolExecutionOutcome.Succeeded, "ok", "{}"));
        Assert.False(FailedToolCompletionGuard.TryGetFailure(Snapshot(corrected), out _));
        Assert.Null(Snapshot(corrected).VerificationEvidence.BlockingCode);
    }

    [Fact]
    public void A_rejected_check_never_blocks_a_later_passing_negative_check()
    {
        var entries = Batch(1, "malformed", "run_process", """{"executable":"python","purpose":"verification"}""",
                ToolExecutionOutcome.Failed, "malformed_call", "{}")
            .Concat(Batch(3, "negative", "bash",
                """{"mode":"shell","script":"exit 1","purpose":"expected_failure","expected_exit_code":1}""",
                ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}"""));
        Assert.Null(Snapshot(entries).VerificationEvidence.BlockingCode);
        Assert.False(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out _));
    }

    [Fact]
    public void A_rejected_rerun_does_not_replace_a_check_that_actually_failed()
    {
        var entries = Batch(1, "test", "bash", Verification,
                ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""")
            .Concat(Batch(3, "rerun", "bash", Verification, ToolExecutionOutcome.Failed, "malformed_call", "{}"))
            .Concat(Batch(5, "lint", "bash", Verification.Replace("dotnet test", "dotnet format"),
                ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}"""));
        Assert.True(FailedToolCompletionGuard.TryGetFailure(Snapshot(entries), out var code));
        Assert.Equal("process_exit_nonzero", code);
    }

    private const string Verification = """{"mode":"shell","script":"dotnet test","purpose":"verification"}""";

    internal static ModelContextSnapshot Snapshot(IEnumerable<ModelContextEntry> entries) => new(entries.ToArray(), Array.Empty<byte>());

    internal static ModelContextEntry[] Batch(long sequence, string id, string tool, string args,
        ToolExecutionOutcome outcome, string code, string content)
    {
        using var json = JsonDocument.Parse(args);
        var call = new ModelToolCall(id, tool, "1.0.0", json.RootElement);
        var receipt = new ToolExecutionReceipt(new ExecutionId("exec_" + new string('a', 32)),
            new ToolCallId("call_" + new string('b', 32)), outcome, code, null, false,
            providerCallId: id);
        return [ModelContextEntry.Assistant("assistant-" + id, sequence,
                    new AssistantStep(null, [call], AssistantFinishReason.ToolCalls, 1)),
            ModelContextEntry.ToolResult("result-" + id, sequence + 1, receipt, content)];
    }
}
