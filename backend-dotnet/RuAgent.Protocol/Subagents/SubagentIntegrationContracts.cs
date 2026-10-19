namespace RuAgent.Protocol.Subagents;

public sealed record SubagentFileChange(string RelativePath, string BaseHash, string ProposedHash,
    ReadOnlyMemory<byte> Content, int Mode);
public sealed record SubagentChangeSet(string AgentId, string ParentRunId, string ParentRoot,
    string SnapshotId, long ResultVersion, IReadOnlyList<SubagentFileChange> Files);
public enum SubagentApplyStatus { Pending, Applied, Conflict, Partial, Unknown }
public sealed record SubagentApplyOutcome(SubagentApplyStatus Status, int AppliedFiles, string Code);

/// <summary>Durably claim a result version/fingerprint before returning null. Never replay pending writes.</summary>
public interface ISubagentApplyJournal
{
    Task<SubagentApplyOutcome?> BeginAsync(SubagentChangeSet changes, string fingerprint, CancellationToken ct);
    Task RecordFileIntentAsync(SubagentChangeSet changes, int fileIndex, CancellationToken ct);
    Task RecordFileReceiptAsync(SubagentChangeSet changes, int fileIndex, CancellationToken ct);
    Task FinishAsync(SubagentChangeSet changes, SubagentApplyOutcome outcome, CancellationToken ct);
}
