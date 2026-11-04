namespace RuAgent.Harness.Core.Subagents;

/// <summary>A parked root's <c>agent_wait</c> call that was just completed durably.</summary>
public sealed record SubagentWaitWake(ToolCallId ToolCallId, bool TimedOut);
