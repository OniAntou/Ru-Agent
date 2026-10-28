using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

public sealed partial class AgentRunCoordinator
{
    private async Task<AgentRunStepResult> RecoverContextOverflowAsync(AgentRun run,
        ModelContextSnapshot context, ModelStepId stepId, CancellationToken ct)
    {
        const string code = "context_window_exceeded";
        if (AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.ContextOverflow))
        {
            var compact = await _compaction.RecoverProviderOverflowAsync(run.RunId, ct).ConfigureAwait(false);
            run = compact.Run ?? run;
            if (compact.Outcome == ContextCompactionOutcome.Committed)
                return await _steps.HandleContextOverflowStepAsync(run, stepId,
                    AgentRecoveryNotes.NoteCount(context, AgentRecoveryKind.ContextOverflow), ct).ConfigureAwait(false);
            if (run.State != HarnessRunState.Running)
                return AgentRunStepResult.Blocked(run, compact.ErrorCode ?? code);
            if (compact.ErrorCode is { } error && error != "compact_not_available")
            {
                await _invocation.FailStepAsync(run, stepId, error).ConfigureAwait(false);
                return AgentRunStepResult.Blocked(await RequireRunAsync(run.RunId, ct).ConfigureAwait(false), error);
            }
        }
        await _invocation.FailStepAsync(run, stepId, code).ConfigureAwait(false);
        return AgentRunStepResult.Blocked(await RequireRunAsync(run.RunId, ct).ConfigureAwait(false), code);
    }
}

internal sealed partial class AgentRunStepHandlers
{
    public async Task<AgentRunStepResult> HandleContextOverflowStepAsync(AgentRun run, ModelStepId stepId,
        int priorNotes, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(AgentRecoveryNotes.BuildContextOverflowNote());
        var payload = new HarnessPayloadIntent(_ids.NewPayloadId(), HarnessPayloadKind.ModelContextNote,
            HarnessPayloadAudience.ModelContext, HarnessPayloadEncoding.Utf8Text, bytes.Length, bytes);
        HarnessEventIntent[] events =
        [new(_ids.NewEventId(), HarnessEventKind.ModelStepCompleted, modelStepId: stepId, safeCode: "context_overflow_recovered"),
         new(_ids.NewEventId(), HarnessEventKind.RunNoteAdded, modelStepId: stepId, payloadId: payload.PayloadId,
             safeCode: "context_window_exceeded")];
        var next = run.Advance(run.Status, run.BudgetWindow, run.ModelStepsInWindow, run.ToolCallsInWindow,
            events.Length, _time.UtcNow);
        var committed = await CommitAsync(run, next, events, [payload], [], ct).ConfigureAwait(false);
        AgentActivityProjector.PublishSafe(_activitySink, run.RunId, AgentActivityProjector.ProjectRetry(stepId, priorNotes + 1));
        return new AgentRunStepResult(committed, []);
    }
}
