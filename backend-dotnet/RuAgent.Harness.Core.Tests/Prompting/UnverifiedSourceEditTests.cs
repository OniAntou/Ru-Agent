using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

public sealed class UnverifiedSourceEditTests
{
    private const string Verification = """{"mode":"shell","script":"dotnet test","purpose":"verification"}""";

    [Fact]
    public void Successful_source_write_without_a_check_awaits_verification_and_is_advised()
    {
        var context = Snapshot(Batch(1, "write", "write_file",
            """{"path":"src/Widget.cs","content":"class Widget {}"}""", ToolExecutionOutcome.Succeeded, "ok", "{}"));

        Assert.True(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.True(UnverifiedSourceEditGuard.ShouldAdvise(context));
        Assert.False(FailedToolCompletionGuard.TryGetFailure(context, out _));
    }

    [Fact]
    public void Undeclared_passing_command_after_an_edit_does_not_clear_the_awaiting_edit()
    {
        var context = Snapshot(Edit(1, "src/a.py")
            .Concat(Batch(3, "run", "powershell", """{"script":"python gen.py"}""",
                ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""")));

        Assert.True(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.True(UnverifiedSourceEditGuard.ShouldAdvise(context));
    }

    [Fact]
    public void Undeclared_failing_command_before_a_successful_edit_still_awaits_verification()
    {
        var context = Snapshot(Batch(1, "build", "powershell", """{"script":"dotnet build"}""",
                ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""")
            .Concat(Edit(3)));

        Assert.True(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.True(UnverifiedSourceEditGuard.ShouldAdvise(context));
    }

    [Fact]
    public void Documentation_only_write_does_not_await_verification()
    {
        var context = Snapshot(Batch(1, "docs", "write_file",
            """{"path":"docs/notes.md","content":"notes"}""", ToolExecutionOutcome.Succeeded, "ok", "{}"));

        Assert.False(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.False(UnverifiedSourceEditGuard.ShouldAdvise(context));
    }

    [Fact]
    public void Read_only_batch_does_not_await_verification()
    {
        var context = Snapshot(Batch(1, "read", "read_file",
            """{"path":"src/Widget.cs"}""", ToolExecutionOutcome.Succeeded, "ok", "{}"));

        Assert.False(context.VerificationEvidence.SourceEditAwaitingVerification);
    }

    [Fact]
    public void Passing_verification_after_an_edit_clears_the_nudge()
    {
        var context = Snapshot(Edit(1).Concat(Passing(3)));

        Assert.False(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.False(UnverifiedSourceEditGuard.ShouldAdvise(context));
    }

    [Fact]
    public void A_passed_check_followed_by_an_edit_is_not_an_obligation_but_is_nudged_once()
    {
        var context = Snapshot(Passing(1).Concat(Edit(3)));

        Assert.True(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.Null(context.VerificationEvidence.BlockingCode);
        Assert.True(UnverifiedSourceEditGuard.ShouldAdvise(context));
    }

    [Fact]
    public void Failing_verification_after_an_edit_is_left_to_the_outstanding_check_path()
    {
        var context = Snapshot(Edit(1).Concat(Failing(3)));

        Assert.False(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.NotNull(context.VerificationEvidence.BlockingCode);
        Assert.False(UnverifiedSourceEditGuard.ShouldAdvise(context));
    }

    [Fact]
    public void Verification_rejected_before_launch_keeps_the_edit_awaiting_without_an_obligation()
    {
        var context = Snapshot(Edit(1).Concat(Batch(3, "malformed", "run_process",
            """{"executable":"python","purpose":"verification"}""", ToolExecutionOutcome.Failed, "malformed_call", "{}")));

        Assert.True(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.Null(context.VerificationEvidence.BlockingCode);
        Assert.True(UnverifiedSourceEditGuard.ShouldAdvise(context));
    }

    [Fact]
    public void Deleting_a_scratch_file_after_a_passing_check_does_not_reopen_the_nudge()
    {
        var context = Snapshot(Edit(1).Concat(Passing(3))
            .Concat(Batch(5, "cleanup", "file_delete", """{"path":"scratch/tmp.py"}""",
                ToolExecutionOutcome.Succeeded, "ok", "{}")));

        Assert.False(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.Null(context.VerificationEvidence.BlockingCode);
        Assert.False(UnverifiedSourceEditGuard.ShouldAdvise(context));
    }

    [Fact]
    public void An_existing_source_edit_note_spends_the_single_nudge()
    {
        var note = ModelContextEntry.Text("source-edit-note", 5, ModelContextRole.System,
            AgentRecoveryNotes.BuildUnverifiedSourceEditNote());
        var context = Snapshot(Edit(1).Append(note));

        Assert.True(context.VerificationEvidence.SourceEditAwaitingVerification);
        Assert.Equal(1, AgentRecoveryNotes.NoteCount(context, AgentRecoveryKind.UnverifiedSourceEdit));
        Assert.False(UnverifiedSourceEditGuard.ShouldAdvise(context));
    }

    [Fact]
    public void Awaiting_flag_is_carried_through_projection()
    {
        var previous = RunVerificationEvidence.Project(Edit(1));
        Assert.True(previous.SourceEditAwaitingVerification);

        var carried = RunVerificationEvidence.Project(Array.Empty<ModelContextEntry>(), previous);
        Assert.True(carried.SourceEditAwaitingVerification);
    }

    [Fact]
    public void A_passing_check_projected_over_previous_evidence_clears_the_carried_flag()
    {
        var previous = RunVerificationEvidence.Project(Edit(1));

        var cleared = RunVerificationEvidence.Project(Passing(3), previous);

        Assert.False(cleared.SourceEditAwaitingVerification);
    }

    [Theory]
    [InlineData("README.md", false)]
    [InlineData("docs/a.RST", false)]
    [InlineData("notes.txt", false)]
    [InlineData("guide.markdown", false)]
    [InlineData("manual.adoc", false)]
    [InlineData("src/a.cs", true)]
    [InlineData("Makefile", true)]
    [InlineData("a.ts", true)]
    public void Write_to_a_path_awaits_verification_only_when_the_path_is_source(string path, bool awaitingVerification)
    {
        var context = Snapshot(Batch(1, "write", "write_file",
            $$"""{"path":"{{path}}","content":"x"}""", ToolExecutionOutcome.Succeeded, "ok", "{}"));

        Assert.Equal(awaitingVerification, context.VerificationEvidence.SourceEditAwaitingVerification);
    }

    [Theory]
    [InlineData("verification_pending", "a required check has no confirmed result")]
    [InlineData("verification_result_missing", "a required check has no confirmed result")]
    public void Verification_unverified_notes_name_the_check_state(string code, string reason)
    {
        var note = AgentRecoveryNotes.BuildUnverifiedNote(code);

        Assert.Contains(reason, note, StringComparison.Ordinal);
        Assert.DoesNotContain("a tool call failed", note, StringComparison.Ordinal);
        Assert.EndsWith("a plain final reply cannot complete the run.", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_failure_unverified_note_keeps_the_tool_failure_reason()
    {
        var note = AgentRecoveryNotes.BuildUnverifiedNote("file_exists");

        Assert.Contains("because a tool call failed", note, StringComparison.Ordinal);
        Assert.EndsWith("a plain final reply cannot complete the run.", note, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_edit_note_starts_with_its_one_time_recovery_marker()
    {
        var note = AgentRecoveryNotes.BuildUnverifiedSourceEditNote();

        Assert.StartsWith("[HARNESS RECOVERY] unverified_source_edit - ", note, StringComparison.Ordinal);
        Assert.Contains("purpose=verification", note, StringComparison.Ordinal);
    }

    private static ModelContextSnapshot Snapshot(IEnumerable<ModelContextEntry> entries) =>
        VerificationCompletionTests.Snapshot(entries);

    private static ModelContextEntry[] Batch(long sequence, string id, string tool, string args,
        ToolExecutionOutcome outcome, string code, string content) =>
        VerificationCompletionTests.Batch(sequence, id, tool, args, outcome, code, content);

    private static ModelContextEntry[] Edit(long sequence, string path = "src/a.cs") =>
        Batch(sequence, "edit-" + sequence, "edit_file", $$"""{"path":"{{path}}","edits":[]}""",
            ToolExecutionOutcome.Succeeded, "ok", "{}");

    private static ModelContextEntry[] Passing(long sequence) =>
        Batch(sequence, "check-" + sequence, "bash", Verification,
            ToolExecutionOutcome.Succeeded, "ok", """{"exit_code":0}""");

    private static ModelContextEntry[] Failing(long sequence) =>
        Batch(sequence, "check-" + sequence, "bash", Verification,
            ToolExecutionOutcome.Failed, "process_exit_nonzero", """{"exit_code":1}""");
}
