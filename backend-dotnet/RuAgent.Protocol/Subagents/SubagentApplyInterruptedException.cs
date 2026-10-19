namespace RuAgent.Protocol.Subagents;

/// <summary>
/// An apply operation lost durable outcome reporting. Callers must enter reconciliation,
/// even when the initiating request was cancelled; a generic failure cannot prove no write.
/// </summary>
public sealed class SubagentApplyInterruptedException(int appliedFiles, Exception innerException)
    : Exception("subagent_apply_reconciliation_required", innerException)
{
    public SubagentApplyOutcome Outcome { get; } = new(SubagentApplyStatus.Unknown, appliedFiles,
        "subagent_apply_reconciliation_required");
}
