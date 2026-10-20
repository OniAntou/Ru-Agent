using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core;

/// <summary>
/// One row of the agent's task list, inside the Core boundary.
/// </summary>
/// <remarks>
/// The Core form of a task row. The Protocol has its own wire form and the Broker its
/// own validated form, and each stays in its own layer: Core does not reference the
/// Broker, so it cannot borrow that one, and reusing the wire record would put
/// serialization attributes in the middle of the domain. Activity kind and state
/// vocabulary comes from Protocol because the values cross the Host/client boundary.
/// </remarks>
public sealed record AgentTodoRow(string Content, string Status);

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
    string? Detail = null,
    IReadOnlyList<AgentTodoRow>? Todos = null);
