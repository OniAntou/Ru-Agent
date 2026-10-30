using System.Text;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentRunStepHandlers
{
    /// <summary>
    /// Commits an explicit blocker: the report is the visible reply, the run fails with
    /// <paramref name="errorCode"/> and no outcome is claimed as successful.
    /// </summary>
    public async Task<AgentRunStepResult> HandleBlockedReportAsync(AgentRun run, ModelStepId stepId,
        AssistantStep step, string report, string errorCode, int droppedCount,
        HarnessPayloadIntent promptPayload, HarnessEventIntent promptEvent, CancellationToken cancellationToken,
        string? reasoningText)
    {
        var transition = AgentRunStateMachine.TryTransition(run.Status, HarnessRunTransition.Fail);
        if (!transition.Accepted) throw new InvalidOperationException(transition.ErrorCode);
        var reply = await _replyPolicy.PrepareAsync(run, report, cancellationToken).ConfigureAwait(false);
        var payloadId = _ids.NewPayloadId();
        var bytes = Encoding.UTF8.GetBytes(reply.VisibleText);
        var payloads = new List<HarnessPayloadIntent>
        {
            promptPayload,
            new(payloadId, HarnessPayloadKind.AssistantText,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Utf8Text, Math.Max(bytes.Length, 1), bytes),
        };
        var events = new List<HarnessEventIntent>
        {
            promptEvent,
            new(_ids.NewEventId(), HarnessEventKind.ModelStepCompleted, modelStepId: stepId,
                payloadId: payloadId, safeCode: "blocked_report"),
        };
        if (step.Usage is not null)
        {
            var usageId = _ids.NewPayloadId();
            var usage = SerializeUsage(step.Usage);
            payloads.Add(new HarnessPayloadIntent(usageId, HarnessPayloadKind.ModelUsage,
                HarnessPayloadAudience.AuthenticatedClient | HarnessPayloadAudience.ModelContext,
                HarnessPayloadEncoding.Json, usage.Length, usage));
            events.Add(new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.ModelUsageRecorded,
                modelStepId: stepId, payloadId: usageId, safeCode: "reported"));
        }
        var (reasoningPayload, reasoningEvent) = BuildReasoningCommit(stepId, reasoningText);
        if (reasoningPayload is not null && reasoningEvent is not null)
        {
            payloads.Add(reasoningPayload);
            events.Add(reasoningEvent);
        }
        if (droppedCount > 0) events.Add(new HarnessEventIntent(_ids.NewEventId(),
            HarnessEventKind.TransientFramesDropped, modelStepId: stepId, safeCount: droppedCount));
        events.Add(new HarnessEventIntent(_ids.NewEventId(), HarnessEventKind.RunFailed, safeCode: errorCode));
        var next = run.Advance(transition.Next, run.BudgetWindow, run.ModelStepsInWindow,
            run.ToolCallsInWindow, events.Count, _time.UtcNow, errorCode);
        var committed = await CommitAsync(run, next, events, payloads,
            _internalRun ? [] : [new ConversationOutboxIntent(_ids.NewEventId(), run.SessionId, payloadId,
                ConversationOutboxRole.Assistant, HistoryWaitKind.FinalBeforeCompletion)], cancellationToken).ConfigureAwait(false);
        return AgentRunStepResult.Blocked(committed, errorCode);
    }
}
