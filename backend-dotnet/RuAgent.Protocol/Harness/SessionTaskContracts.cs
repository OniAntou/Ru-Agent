namespace RuAgent.Protocol;

/// <summary>
/// One session-task control request crossing the Host/Broker boundary: stop one
/// task, or page one task's retained output. The verbs are mutually exclusive,
/// and both fail closed for ids the session does not own. Paging bounds are
/// enforced where the text lives, so the Host only forwards what a client
/// asked for.
/// </summary>
public sealed record SessionTaskRequest(
    string? KillExecutionId = null,
    string? ReadExecutionId = null,
    int? Offset = null,
    int? Limit = null);
