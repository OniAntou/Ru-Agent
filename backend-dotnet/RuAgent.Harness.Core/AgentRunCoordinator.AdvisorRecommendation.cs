using RuAgent.Harness.Core.Completion;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;

namespace RuAgent.Harness.Core;

public sealed partial class AgentRunCoordinator
{
    /// <summary>
    /// The one-time advisor recommendation (ADR 0047, owner's decision 2026-11-14). A weak model
    /// rarely asks for advice on its own, so when a root run with a selected advisor is about to
    /// finish and the advisor has not seen the work since the last tool call, the reply is held
    /// once with a note recommending a consultation. The model decides: it may call
    /// <c>advisor</c>, or give its final reply again, which completes the run. The Host never
    /// consults on the model's behalf.
    /// </summary>
    /// <returns>The hold to apply, or null to complete.</returns>
    private StepDecision? RecommendAdvisor(AgentModelInvocation.ModelInvocationResult invocation, ModelContextSnapshot context)
    {
        if (!_recommendAdvisor
            // The tool must be callable in this step, or the recommendation could not be followed.
            || invocation.PromptContext?.Capabilities.Any(capability => capability.Id == AdvisorCodes.ToolId) != true
            || !HasWorkTheAdvisorHasNotSeen(context)
            || !AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.AdvisorRecommended))
            return null;

        return new StepDecision(StepDecisionKind.Hold, "R10.advisor_recommended",
            RetractStreamedText: true,
            NoteKind: AgentRecoveryKind.AdvisorRecommended,
            NoteText: AgentRecoveryNotes.BuildAdvisorRecommendedNote(),
            PriorNoteCount: AgentRecoveryNotes.NoteCount(context, AgentRecoveryKind.AdvisorRecommended),
            // The draft stays in view, so declining costs one repeated reply, not a restart.
            RetainAssistantContext: true);
    }

    /// <summary>
    /// True when the context shows tool work after the latest advisor call. A run with no tool
    /// work has nothing for the advisor to check, and a consultation (answered or failed with a
    /// named code) after the last other tool call already covers the finished work.
    /// </summary>
    internal static bool HasWorkTheAdvisorHasNotSeen(ModelContextSnapshot context)
    {
        for (var index = context.Entries.Count - 1; index >= 0; index--)
        {
            if (context.Entries[index].AssistantStep is not { ToolCalls.Count: > 0 } step) continue;
            // advisor must be called alone, so one step is either a consultation or other work.
            return step.ToolCalls.All(call => call.ToolId != AdvisorCodes.ToolId);
        }

        return false;
    }
}
