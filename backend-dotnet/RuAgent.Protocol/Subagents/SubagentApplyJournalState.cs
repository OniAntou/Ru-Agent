namespace RuAgent.Protocol.Subagents;

public sealed record SubagentApplyFileIntent(int FileIndex, string RelativePath, string BaseHash,
    string ProposedHash, int Mode, bool Receipted);
public sealed record SubagentApplyJournalState(string Fingerprint, SubagentApplyOutcome Outcome,
    IReadOnlyList<SubagentApplyFileIntent> Files);

/// <summary>Inspection only. Intent or matching disk bytes never establish a durable receipt.</summary>
public interface ISubagentApplyJournalReader
{
    Task<SubagentApplyJournalState?> ReadAsync(string rootRunId, string agentId, long resultVersion, CancellationToken ct);
}
