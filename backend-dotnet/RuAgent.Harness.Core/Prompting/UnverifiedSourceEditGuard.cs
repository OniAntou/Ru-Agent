using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Prompting;

/// <summary>
/// One-time soft nudge: after source edits with no declared verification since, the
/// first final reply is held once with a recovery note. It is never a failure path.
/// While a check is outstanding, the completion guard already decides, so this stays silent.
/// </summary>
public static class UnverifiedSourceEditGuard
{
    public static bool ShouldAdvise(ModelContextSnapshot context) =>
        context.VerificationEvidence.BlockingCode is null
        && context.VerificationEvidence.SourceEditAwaitingVerification
        && AgentRecoveryNotes.CanAdvise(context, AgentRecoveryKind.UnverifiedSourceEdit);
}
