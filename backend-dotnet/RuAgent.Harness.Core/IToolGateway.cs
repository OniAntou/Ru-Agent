namespace RuAgent.Harness.Core;

public interface IToolGateway
{
    Task<ToolProposalDecision> EvaluateAsync(
        ToolProposal proposal,
        CancellationToken cancellationToken);

    Task<PreparedToolExecution> PrepareAsync(
        ApprovedToolExecution execution,
        CancellationToken cancellationToken);

    Task<ToolExecutionReceipt> ExecuteAsync(
        PreparedToolExecution execution,
        Func<ToolOutputChunk, ValueTask> publishDelta,
        CancellationToken cancellationToken);

    Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken);
}
