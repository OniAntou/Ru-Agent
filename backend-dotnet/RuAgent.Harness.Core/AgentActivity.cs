namespace RuAgent.Harness.Core;

public enum AgentActivityKind
{
    Thinking,
    Explore,
    Search,
    Analyze,
    Edit,
    Terminal,
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
    string? ToolDisplay = null);
