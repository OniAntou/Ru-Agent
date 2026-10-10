using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentRunStepHandlers
{
    public Task<AgentRunStepResult> HandleUnusableModelStepAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        int droppedCount,
        ModelContextSnapshot context,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken,
        string? reasoningText)
    {
        var code = step.FinishReason switch
        {
            AssistantFinishReason.Length => "model_output_truncated",
            AssistantFinishReason.ContentFilter => "model_content_filtered",
            AssistantFinishReason.Error => "model_reply_error",
            _ => throw new ArgumentOutOfRangeException(nameof(step)),
        };
        return HandleModelReplyFailureAsync(run, modelStepId, step, droppedCount,
            context, promptReceiptPayload, promptComposedEvent, cancellationToken,
            reasoningText, code, step.FinishReason == AssistantFinishReason.Length
                ? AgentRecoveryKind.OutputTruncated : null);
    }

    private async Task<AgentRunStepResult> HandleModelReplyFailureAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AssistantStep step,
        int droppedCount,
        ModelContextSnapshot context,
        HarnessPayloadIntent promptReceiptPayload,
        HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken,
        string? reasoningText = null,
        string errorCode = "empty_model_reply",
        AgentRecoveryKind? recoveryKind = AgentRecoveryKind.EmptyModelReply)
    {
        if (recoveryKind is { } kind && AgentRecoveryNotes.CanAdvise(context, kind))
        {
            return await HandleRecoveryAdviceStepAsync(
                    run,
                    modelStepId,
                    step,
                    droppedCount,
                    kind == AgentRecoveryKind.OutputTruncated
                        ? AgentRecoveryNotes.BuildTruncatedNote()
                        : AgentRecoveryNotes.BuildEmptyNote(),
                    AgentRecoveryNotes.NoteCount(context, kind),
                    errorCode,
                    promptReceiptPayload,
                    promptComposedEvent,
                    cancellationToken,
                    reasoningText)
                .ConfigureAwait(false);
        }

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
