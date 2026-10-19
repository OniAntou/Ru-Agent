using RuAgent.Protocol.Subagents;

namespace RuAgent.Harness.Core.Subagents;

/// <summary>Read-only family data. Model metadata deliberately excludes credentials and request configuration.</summary>
public sealed record SubagentSnapshot(AgentRun Root, bool Canceled,
    IReadOnlyList<SubagentSnapshotEntry> Agents, IReadOnlyList<SubagentPreparationSnapshot> Preparations,
    int UnavailableAttempts);

public sealed record SubagentSnapshotEntry(SubagentRecord Agent, string? Task,
    IReadOnlyList<SubagentAttemptSnapshot> Attempts, IReadOnlyList<SubagentResultSnapshot> Results);

public sealed record SubagentAttemptSnapshot(SubagentAttempt Attempt, SubagentPinnedModel? Model, SubagentPendingApproval? Approval = null);
public sealed record SubagentPendingApproval(ApprovalId ApprovalId, string ToolId, System.Text.Json.JsonElement Arguments, bool ArgumentsTruncated = false);
public sealed record SubagentPinnedModel(string ProviderId, string Model, string Protocol);
public sealed record SubagentResultSnapshot(SubagentResult Result, SubagentApplyOutcome? Apply, bool Acknowledged);
public sealed record SubagentPreparationSnapshot(string AgentId, SubagentWorkspaceAllocationStage Stage);
public sealed record SubagentResultPage(string RootRunId, string AgentId, long ResultVersion,
    string Text, int Offset, int TotalBytes, int? NextOffset);
