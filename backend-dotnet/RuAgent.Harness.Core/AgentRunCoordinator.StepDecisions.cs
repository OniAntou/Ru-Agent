using RuAgent.Harness.Core.Completion;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

public sealed partial class AgentRunCoordinator
{
    /// <summary>
    /// Executes one decided outcome with its existing commit handler. The decision itself is
    /// made by <see cref="CompletionPolicy"/>; this method is the only mapping from a decision
    /// kind to a handler, and it retracts streamed prose exactly when the decision says so.
    /// </summary>
    private async Task<AgentRunStepResult> ExecuteStepDecisionAsync(
        StepDecision decision,
        AgentRun run,
        ModelStepId modelStepId,
        AgentModelInvocation.ModelInvocationResult invocation,
        ModelContextSnapshot context,
        CancellationToken cancellationToken)
    {
        if (decision.RetractStreamedText)
            invocation.RetractAssistantText();

        var step = invocation.Step;
        var droppedCount = invocation.ModelDroppedCount;
        var promptReceiptPayload = invocation.PromptReceiptPayload;
        var promptComposedEvent = invocation.PromptComposedEvent;
        var reasoningText = invocation.ReasoningText;
        switch (decision.Kind)
        {
            case StepDecisionKind.Proceed:
                return await DispatchToolCallsAsync(run, modelStepId, invocation, context, cancellationToken)
                    .ConfigureAwait(false);
            case StepDecisionKind.Hold:
                return await _steps.HandleHoldStepAsync(
                        run, modelStepId, step, droppedCount, decision,
                        promptReceiptPayload, promptComposedEvent, cancellationToken, reasoningText)
                    .ConfigureAwait(false);
            case StepDecisionKind.Fail:
                return await _steps.HandleModelReplyFailureAsync(
                        run, modelStepId, step, droppedCount,
                        promptReceiptPayload, promptComposedEvent, cancellationToken, reasoningText,
                        decision.ErrorCode!)
                    .ConfigureAwait(false);
            case StepDecisionKind.UnverifiedFail:
                return await _steps.HandleUnverifiedFailStepAsync(
                        run, modelStepId, step, decision.ErrorCode!, droppedCount,
                        promptReceiptPayload, promptComposedEvent, cancellationToken, reasoningText)
                    .ConfigureAwait(false);
            case StepDecisionKind.BlockedReport:
                return await _steps.HandleBlockedReportAsync(
                        run, modelStepId, step, decision.ReportText!, decision.ErrorCode!, droppedCount,
                        promptReceiptPayload, promptComposedEvent, cancellationToken, reasoningText)
                    .ConfigureAwait(false);
            case StepDecisionKind.Complete:
                return await _steps.HandleFinalStepAsync(
                        run, modelStepId, step, droppedCount,
                        promptReceiptPayload, promptComposedEvent, cancellationToken, reasoningText,
                        invocation.ProviderContinuation)
                    .ConfigureAwait(false);
            default:
                throw new InvalidOperationException("unknown_step_decision");
        }
    }

    /// <summary>
    /// Tool calls keep their dispatch path. The repetition guard may answer with one repair
    /// note or park the run; otherwise the calls are dispatched. Repetition is not part of
    /// the completion policy and is evaluated against the pre-refresh context, as before.
    /// </summary>
    private async Task<AgentRunStepResult> DispatchToolCallsAsync(
        AgentRun run,
        ModelStepId modelStepId,
        AgentModelInvocation.ModelInvocationResult invocation,
        ModelContextSnapshot context,
        CancellationToken cancellationToken)
    {
        var step = invocation.Step;
        var repetition = ConsecutiveToolCallGuard.Inspect(
            context,
            step.ToolCalls,
            out var receiptCode);
        if (repetition == RepetitionGuardVerdict.Advise)
        {
            invocation.RetractAssistantText();
            return await _steps.HandleRepetitionAdviceStepAsync(
                    run,
                    modelStepId,
                    step,
                    receiptCode,
                    invocation.ModelDroppedCount,
                    invocation.PromptReceiptPayload,
                    invocation.PromptComposedEvent,
                    cancellationToken,
                    invocation.ReasoningText)
                .ConfigureAwait(false);
        }

        if (repetition == RepetitionGuardVerdict.Stop)
        {
            invocation.RetractAssistantText();
            return await _steps.HandleRepeatedToolStepAsync(
                    run,
                    modelStepId,
                    step,
                    invocation.PromptReceiptPayload,
                    invocation.PromptComposedEvent,
                    cancellationToken,
                    invocation.ReasoningText)
                .ConfigureAwait(false);
        }

        return await _steps.HandleToolStepAsync(
                run,
                modelStepId,
                step,
                invocation.ModelDroppedCount,
                invocation.PromptReceiptPayload,
                invocation.PromptComposedEvent,
                cancellationToken,
                invocation.ReasoningText,
                invocation.ProviderContinuation)
            .ConfigureAwait(false);
    }
}
