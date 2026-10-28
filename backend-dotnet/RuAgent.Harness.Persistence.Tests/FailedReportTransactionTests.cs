using System.Text;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using Xunit;

namespace RuAgent.Harness.Persistence.Tests;

public sealed class FailedReportTransactionTests
{
    [Theory]
    [InlineData("blocked_report", "expected_exit_mismatch", false, false, true)]
    [InlineData("other_report", "expected_exit_mismatch", false, false, false)]
    [InlineData("blocked_report", "process_exit_nonzero", false, false, false)]
    [InlineData("blocked_report", "expected_exit_mismatch", true, false, false)]
    [InlineData("blocked_report", "expected_exit_mismatch", false, true, false)]
    public async Task Only_an_atomic_failed_assistant_report_may_enqueue_terminal_history(
        string marker, string failureCode, bool userPayload, bool userOutbox, bool accepted)
    {
        await using var fixture = await HarnessStoreTestFixture.CreateAsync();
        await fixture.RegisterWorkspaceAsync(1);
        var store = fixture.CreateEventStore();
        var request = fixture.NewRun(1, 1, 1, status: new HarnessRunStatus(HarnessRunState.Running));
        await store.TryCreateRunAsync(request, default);
        var payloadId = HarnessStoreTestFixture.Payload(100);
        var bytes = Encoding.UTF8.GetBytes("expected 9, observed 7");
        var payload = new HarnessPayloadIntent(payloadId,
            userPayload ? HarnessPayloadKind.UserPrompt : HarnessPayloadKind.AssistantText,
            HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
            HarnessPayloadEncoding.Utf8Text, 1024, bytes);
        var next = HarnessEventStoreTests.CopyRun(request.Run, new HarnessRunStatus(HarnessRunState.Failed),
            1, 3, request.Run.UpdatedAt.AddSeconds(1), "expected_exit_mismatch");
        var commit = new HarnessCommit(request.Run.RunId, 0, next,
            [new(HarnessStoreTestFixture.Event(101), HarnessEventKind.ModelStepCompleted,
                payloadId: payloadId, safeCode: marker),
             new(HarnessStoreTestFixture.Event(102), HarnessEventKind.RunFailed, safeCode: failureCode)],
            [payload],
            [new(HarnessStoreTestFixture.Event(103), request.Run.SessionId, payloadId,
                userOutbox ? ConversationOutboxRole.User : ConversationOutboxRole.Assistant,
                userOutbox ? HistoryWaitKind.UserBeforeFirstStep : HistoryWaitKind.FinalBeforeCompletion)]);

        if (accepted)
            Assert.Equal(HarnessCommitOutcome.Committed, (await store.CommitAsync(commit, default)).Outcome);
        else
            await Assert.ThrowsAsync<HarnessStoreException>(() => store.CommitAsync(commit, default));

        var reopened = fixture.CreateEventStore();
        var loaded = await reopened.GetRunAsync(request.Run.RunId, default);
        Assert.Equal(accepted ? HarnessRunState.Failed : HarnessRunState.Running, loaded!.State);
        Assert.Equal(accepted ? "expected_exit_mismatch" : null, loaded.TerminalErrorCode);
        var outbox = new HarnessOutboxStore(fixture.Options);
        var claim = await outbox.ClaimNextAsync(request.Run.UpdatedAt.AddMinutes(1), default);
        if (accepted) Assert.Equal(payloadId, claim!.PayloadId);
        else Assert.Null(claim);
    }
}
