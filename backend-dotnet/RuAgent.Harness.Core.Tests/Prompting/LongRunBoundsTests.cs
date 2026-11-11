using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;
using Xunit;

namespace RuAgent.Harness.Core.Tests.Prompting;

/// <summary>Bounds that must measure recent trouble, never how long a run has been working.</summary>
public sealed class LongRunBoundsTests
{
    [Fact]
    public void Recovery_notes_far_behind_the_newest_entry_no_longer_spend_the_bound()
    {
        var old = Enumerable.Range(0, AgentRecoveryNotes.MaximumNotesPerWindow)
            .Select(index => Note(1 + index, AgentRecoveryNotes.BuildEmptyNote()));
        var work = ModelContextEntry.Text("user-late", 1 + AgentRecoveryNotes.MaximumNotesPerWindow
            + AgentRecoveryNotes.RecoveryWindowSequences + 1, ModelContextRole.User, "keep going");
        var context = VerificationCompletionTests.Snapshot(old.Append(work));

        Assert.Equal(AgentRecoveryNotes.MaximumNotesPerWindow, AgentRecoveryNotes.NoteCount(context));
        Assert.Equal(0, AgentRecoveryNotes.RecentNoteCount(context, null));
        Assert.True(AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.EmptyModelReply));
    }

    [Fact]
    public void Recovery_notes_inside_the_window_still_spend_the_case_bound()
    {
        var recent = Enumerable.Range(0, AgentRecoveryNotes.MaximumEmptyNotes)
            .Select(index => Note(10 + index, AgentRecoveryNotes.BuildEmptyNote()));
        var context = VerificationCompletionTests.Snapshot(recent);

        Assert.Equal(AgentRecoveryNotes.MaximumEmptyNotes,
            AgentRecoveryNotes.RecentNoteCount(context, AgentRecoveryKind.EmptyModelReply));
        Assert.False(AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.EmptyModelReply));
    }

    [Fact]
    public void One_time_completion_nudges_stay_once_per_run_however_old_the_note_is()
    {
        var note = Note(1, AgentRecoveryNotes.BuildCompletionReviewNote());
        var work = ModelContextEntry.Text("user-late", AgentRecoveryNotes.RecoveryWindowSequences + 10,
            ModelContextRole.User, "keep going");
        var context = VerificationCompletionTests.Snapshot(new[] { note, work });

        Assert.False(AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.CompletionReview));
    }

    [Fact]
    public void More_passed_checks_than_the_tracking_limit_do_not_block_completion()
    {
        var entries = new List<ModelContextEntry>();
        for (var index = 0; index <= RunVerificationEvidence.MaximumChecks; index++)
        {
            entries.AddRange(VerificationCompletionTests.Batch(1 + index * 2, "check-" + index, "bash",
                "{\"mode\":\"shell\",\"script\":\"dotnet test --filter Case" + index + "\",\"purpose\":\"verification\"}",
                ToolExecutionOutcome.Succeeded, "ok", "{\"exit_code\":0}"));
        }

        var evidence = VerificationCompletionTests.Snapshot(entries).VerificationEvidence;

        Assert.False(evidence.Overflowed);
        Assert.Null(evidence.BlockingCode);
        Assert.Equal(RunVerificationEvidence.MaximumChecks, evidence.Checks.Count);
    }

    [Fact]
    public void A_snapshot_that_differs_only_in_receipt_bookkeeping_is_not_sent_again()
    {
        const string retained = "RUNTIME CONTEXT SNAPSHOT\n[session_work_state workspace=abc version=3 untrusted=true]\n"
            + "User goal [run:r; event:e]: fix pricing\nReceipt-observed file: a.py sha256:1 run:r event:e1 at:t1";
        const string bookkeepingOnly = "RUNTIME CONTEXT SNAPSHOT\n[session_work_state workspace=abc version=4 untrusted=true]\n"
            + "User goal [run:r; event:e]: fix pricing\nReceipt-observed file: a.py sha256:2 run:r event:e2 at:t2\n"
            + "Recorded verification: command=pytest; outcome=ok; succeeded=True; run:r";
        const string newGoal = "RUNTIME CONTEXT SNAPSHOT\n[session_work_state workspace=abc version=5 untrusted=true]\n"
            + "User goal [run:r; event:e9]: also add a CLI";
        var context = VerificationCompletionTests.Snapshot(new[]
        {
            ModelContextEntry.Text(RuntimeContextSnapshot.SourcePrefix + "one", 3, ModelContextRole.User, retained),
        });

        Assert.Null(RuntimeContextSnapshot.Pending(context, retained));
        Assert.Null(RuntimeContextSnapshot.Pending(context, bookkeepingOnly));
        Assert.Equal(newGoal, RuntimeContextSnapshot.Pending(context, newGoal));
        Assert.Equal(RuntimeContextSnapshot.Cleared, RuntimeContextSnapshot.Pending(context, null));
    }

    private static ModelContextEntry Note(long sequence, string text) =>
        ModelContextEntry.Text("note-" + sequence, sequence, ModelContextRole.System, text);
}
