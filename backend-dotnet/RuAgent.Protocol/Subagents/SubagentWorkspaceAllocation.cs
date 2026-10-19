namespace RuAgent.Protocol.Subagents;

public enum SubagentWorkspaceAllocationStage
{
    Reserved,
    SnapshotAllocated,
    RegisteringWorktree,
    WorktreeRegistered,
    Prepared,
}

/// <summary>Cleanup evidence only; never restores execution policy, model selection or a run.</summary>
public sealed record SubagentWorkspaceAllocation(string RootRunId, string AgentId, string SnapshotId,
    SubagentWorkspaceAllocationStage Stage, string? SnapshotJson = null, string? WorktreeManifestJson = null)
{
    public const int MaximumEvidenceBytes = 64 * 1024;
}

/// <summary>Trusted in-process observer. Every transition completes before the corresponding side effects.</summary>
public interface ISubagentWorkspaceAllocationObserver
{
    SubagentWorkspaceAllocation Reservation { get; }
    Task RecordAsync(SubagentWorkspaceAllocation evidence, CancellationToken cancellationToken);
}
