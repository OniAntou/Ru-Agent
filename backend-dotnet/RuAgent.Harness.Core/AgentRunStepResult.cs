using System.Collections.ObjectModel;

namespace RuAgent.Harness.Core;

public sealed record AgentRunStepResult
{
    public AgentRunStepResult(
        AgentRun run,
        IReadOnlyList<ToolExecutionReceipt> receipts,
        string? errorCode = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(receipts);
        var copiedReceipts = receipts.ToArray();
        if (copiedReceipts.Any(receipt => receipt is null))
        {
            throw new ArgumentException("Receipts cannot contain null.", nameof(receipts));
        }

        Run = run;
        Receipts = new ReadOnlyCollection<ToolExecutionReceipt>(copiedReceipts);
        ErrorCode = errorCode;
    }

    public AgentRun Run { get; }
    public IReadOnlyList<ToolExecutionReceipt> Receipts { get; }
    public string? ErrorCode { get; }

    public static AgentRunStepResult Blocked(AgentRun run, string errorCode) =>
        new(run, Array.Empty<ToolExecutionReceipt>(), errorCode);
}
