namespace RuAgent.Protocol.Subagents;

/// <summary>Read observations only. A matching hash does not establish a write receipt or permission.</summary>
public sealed record SubagentApplyFileObservation(int FileIndex, string RelativePath, string? CurrentHash,
    string Match, bool IntentRecorded, bool Receipted);

public sealed record SubagentApplyInspection(string Fingerprint, string ObservationHash,
    IReadOnlyList<SubagentApplyFileObservation> Files);
