namespace RuAgent.Harness.Core.Subagents;

/// <summary>
/// What one child is doing, attached to an <c>agent_wait</c> that timed out. Every text
/// field is bounded display data derived from the child's own activity, so it is as
/// untrusted as the child's results.
/// </summary>
public sealed record SubagentWaitProgress(
    string AgentId,
    RunId RunId,
    string State,
    int ModelSteps,
    int ToolCalls,
    IReadOnlyList<string> RecentActivity,
    string? LatestNarration);

/// <summary>A parked root's wait as seen before it is completed.</summary>
public sealed record SubagentWaitProbe(IReadOnlyList<(string AgentId, RunId RunId)> Attempts, bool TimesOutNow);
