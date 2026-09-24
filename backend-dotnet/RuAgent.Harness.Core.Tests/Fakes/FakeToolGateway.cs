using RuAgent.Harness.Core.Persistence;

namespace RuAgent.Harness.Core.Tests.Fakes;

public sealed class FakeToolGateway : IToolGateway, IApprovalCleanupGateway
{
    private readonly IList<string>? _trace;
    private int _remainingTransportFailures;

    public FakeToolGateway(
        ToolAccessKind accessKind = ToolAccessKind.ReadOnly,
        int transportFailures = 0,
        IList<string>? trace = null)
    {
        AccessKind = accessKind;
        _remainingTransportFailures = transportFailures;
        _trace = trace;
    }

    public ToolAccessKind AccessKind { get; }

    /// <summary>Overrides the access kind stamped onto prepared executions when set.</summary>
    public ToolAccessKind? PreparedKind { get; set; }

    public IReadOnlyList<PreparedPayload> PreparationPayloads { get; set; } =
        Array.Empty<PreparedPayload>();

    public List<ToolProposal> Proposals { get; } = new();

    public List<ExecutionId> ExecuteAttempts { get; } = new();

    public List<ApprovedToolExecution> ApprovedExecutions { get; } = new();

    public List<PreparedToolExecution> PreparedExecutions { get; } = new();

    public List<ExecutionId> CancelAttempts { get; } = new();

    public List<ApprovalId> RevokedApprovals { get; } = new();

    public IReadOnlyList<string> OutputChunks { get; set; } = [];

    public Func<ToolProposal, ToolProposalDecision>? DecisionFactory { get; set; }

    /// <summary>When set, returned from every ExecuteAsync call with the live identities.</summary>
    public ToolExecutionReceipt? ForcedReceipt { get; set; }

    public Task<ToolProposalDecision> EvaluateAsync(
        ToolProposal proposal,
        CancellationToken cancellationToken)
    {
        Proposals.Add(proposal);
        _trace?.Add("evaluate");
        return Task.FromResult(DecisionFactory?.Invoke(proposal)
            ?? ToolProposalDecision.Allow(
                PreparedKind ?? AccessKind,
                $"canonical-{proposal.ModelOrder}"));
    }

    public Task<PreparedToolExecution> PrepareAsync(
        ApprovedToolExecution execution,
        CancellationToken cancellationToken)
    {
        _trace?.Add("prepare");
        ApprovedExecutions.Add(execution);
        var prepared = new PreparedToolExecution(
            execution.ExecutionId,
            execution.Proposal.ToolCallId,
            execution.Proposal.ModelOrder,
            PreparedKind ?? AccessKind,
            $"prepared-{execution.Proposal.ModelOrder}",
            PreparationPayloads,
            execution.GatewayGeneration);
        PreparedExecutions.Add(prepared);
        return Task.FromResult(prepared);
    }

    public async Task<ToolExecutionReceipt> ExecuteAsync(
        PreparedToolExecution execution,
        Func<ToolOutputChunk, ValueTask> publishDelta,
        CancellationToken cancellationToken)
    {
        ExecuteAttempts.Add(execution.ExecutionId);
        _trace?.Add("execute");
        if (ForcedReceipt is { } forced)
        {
            return new ToolExecutionReceipt(
                execution.ExecutionId,
                execution.ToolCallId,
                forced.Outcome,
                forced.Code,
                forced.ResultPayloadId,
                forced.SideEffectsPossible,
                forced.ModelContent,
                forced.ContentEncoding);
        }
        if (_remainingTransportFailures > 0)
        {
            _remainingTransportFailures--;
            throw new ToolTransportException("fake_transport_failure");
        }

        for (var index = 0; index < OutputChunks.Count; index++)
        {
            await publishDelta(new ToolOutputChunk(
                execution.ExecutionId,
                index + 1,
                ToolOutputStream.Stdout,
                OutputChunks[index]));
        }

        return new ToolExecutionReceipt(
            execution.ExecutionId,
            execution.ToolCallId,
            ToolExecutionOutcome.Succeeded,
            "ok",
            null,
            sideEffectsPossible: false,
            modelContent: "{\"content\":\"fake result\"}"u8.ToArray(),
            contentEncoding: HarnessPayloadEncoding.Json);
    }

    public Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken)
    {
        CancelAttempts.Add(executionId);
        _trace?.Add("cancel");
        return Task.CompletedTask;
    }

    public Task<bool> RevokeApprovalAsync(
        ApprovalId approvalId,
        CancellationToken cancellationToken)
    {
        RevokedApprovals.Add(approvalId);
        return Task.FromResult(true);
    }
}
