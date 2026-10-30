using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentRunStepHandlers
{
    /// <summary>
    /// Commits a terminal model-reply failure (unusable finish, commentary-only or empty reply)
    /// once the policy has decided that no recovery note is left. The model step is audit-only
    /// and the run fails with <paramref name="errorCode"/>.
    /// </summary>
    public async Task<AgentRunStepResult> HandleModelReplyFailureAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        int droppedCount,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken,
        string? reasoningText,
        string errorCode)
    {
        var transition = AgentRunStateMachine.TryTransition(
            run.Status,
            HarnessRunTransition.Fail);
        if (!transition.Accepted)
        {
            throw new InvalidOperationException(transition.ErrorCode);
        }

        var assistantPayloadId = _ids.NewPayloadId();
        var assistantBytes = AssistantStepJsonCodec.Serialize(step);
        var assistantPayload = new HarnessPayloadIntent(
            assistantPayloadId,
            HarnessPayloadKind.AssistantStep,
            // Unusable output is audit-only; it must not be published as a completed reply.
            HarnessPayloadAudience.InternalOnly,
            HarnessPayloadEncoding.Json,
            Math.Max(assistantBytes.Length, 1),
            assistantBytes);
        var events = new List<HarnessEventIntent>
        {
            promptComposedEvent,
            new(
                _ids.NewEventId(),
                HarnessEventKind.ModelStepCompleted,
                modelStepId: modelStepId,
                payloadId: assistantPayloadId,
                safeCount: step.ToolCalls.Count,
                safeCode: errorCode),
            new(
                _ids.NewEventId(),
                HarnessEventKind.RunFailed,
                safeCode: errorCode),
        };
        var (reasoningPayload, reasoningEvent) = BuildReasoningCommit(modelStepId, reasoningText);
        var commitPayloads = new List<HarnessPayloadIntent> { promptReceiptPayload, assistantPayload };
        if (reasoningEvent is not null && reasoningPayload is not null)
        {
            events.Insert(2, reasoningEvent);
            commitPayloads.Add(reasoningPayload);
        }
        if (droppedCount > 0)
        {
            events.Insert(events.Count - 1, new HarnessEventIntent(
                _ids.NewEventId(), HarnessEventKind.TransientFramesDropped,
                modelStepId: modelStepId, safeCount: droppedCount));
        }
        var next = run.Advance(
            transition.Next,
            run.BudgetWindow,
            run.ModelStepsInWindow,
            run.ToolCallsInWindow,
            events.Count,
            _time.UtcNow,
            errorCode);
        var committed = await CommitAsync(
                run,
                next,
                events,
                commitPayloads,
                Array.Empty<ConversationOutboxIntent>(),
                cancellationToken)
            .ConfigureAwait(false);
        return AgentRunStepResult.Blocked(committed, errorCode);
    }

}
