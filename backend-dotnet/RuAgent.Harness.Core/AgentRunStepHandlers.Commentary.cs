using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Persistence;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

internal sealed partial class AgentRunStepHandlers
{
    public Task<AgentRunStepResult> HandleCommentaryStepAsync(AgentRun run, ModelStepId modelStepId,
        AssistantStep step, int droppedCount, ModelContextSnapshot context,
        HarnessPayloadIntent promptReceiptPayload, HarnessEventIntent promptComposedEvent,
        CancellationToken cancellationToken, string? reasoningText) =>
        HandleModelReplyFailureAsync(run, modelStepId, step, droppedCount, context,
            promptReceiptPayload, promptComposedEvent, cancellationToken, reasoningText,
            "model_commentary_only", AgentRecoveryKind.CommentaryOnly);
}
