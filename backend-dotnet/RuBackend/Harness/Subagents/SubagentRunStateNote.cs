using RuAgent.Harness.Core;
using RuAgent.Protocol.Subagents;

namespace RuBackend.Harness.Subagents;

/// <summary>
/// One detached child as the model-facing run-state note describes it. Activity (is the child
/// still working?) and result readiness (is there a result a later root can apply?) are separate
/// facts, so a completed child with a collected result is not reported as merely "completed".
/// </summary>
public sealed record SubagentRunStateFact(
    string AgentId,
    SubagentRole Role,
    string Activity,
    long? ResultVersion = null,
    HarnessRunState? ResultState = null);

/// <summary>
/// Bounded run-state line for detached children of earlier roots (ADR 0045). This is the Ru
/// adaptation of DSH's subagent-settled notice: detached children outlive their creator, so the
/// notice lives in the session-visible run state instead of a live parent inbox.
/// </summary>
public static class SubagentRunStateNote
{
    public const int MaxFacts = 8;

    public static string? Describe(IReadOnlyList<SubagentRunStateFact>? facts, bool more = false)
    {
        if (facts is null || facts.Count == 0) return null;
        var shown = facts.Take(MaxFacts).Select(DescribeFact);
        return "detached from earlier runs in this session: " + string.Join(", ", shown)
            + (more ? ", …" : string.Empty);
    }

    private static string DescribeFact(SubagentRunStateFact fact)
    {
        var result = fact.ResultVersion is { } version
            ? $", result v{version} {(fact.ResultState is { } state ? Lower(state) : "unknown")}"
            : string.Empty;
        return $"{fact.AgentId} ({fact.Role}, {fact.Activity}{result})";
    }

    private static string Lower(HarnessRunState state) => state.ToString().ToLowerInvariant();
}
