namespace RuAgent.Harness.Core;

public enum AgentActivityKind
{
    Thinking,
    Explore,
    Search,
    Analyze,
    Edit,
    Terminal,
    Compaction,
    Retry,
    Status,
}

public enum AgentActivityState
{
    Started,
    Completed,
    Failed,
    Waiting,
}

/// <summary>
/// Safe activity data created inside the Core boundary. It intentionally has no raw
/// command, argument, path, patch, output or model-reasoning fields.
/// Detail carries short human-readable context (for example a model thinking
/// excerpt); it is length-bounded display text, never tool input/output.
/// </summary>
public sealed record AgentActivity(
    string ActivityId,
    AgentActivityKind Kind,
    AgentActivityState State,
    string? FileName = null,
    int? LineStart = null,
    int? LineEnd = null,
    string? Symbol = null,
    int? Count = null,
    int? Added = null,
    int? Removed = null,
    int? DurationMs = null,
    string SafeCode = "succeeded",
    string? ToolDisplay = null,
    string? Detail = null);
