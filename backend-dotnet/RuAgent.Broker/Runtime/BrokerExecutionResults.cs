using System.Text.Json.Nodes;
using RuAgent.Broker.Policy;
using RuAgent.Protocol;

namespace RuAgent.Broker.Runtime;

/// <summary>
/// A precondition failure that maps to the shared tool-error envelope shape
/// (status denied, error_code, safe_summary) rather than a message-specific
/// result payload.
/// </summary>
public sealed record RuntimeError(string ErrorCode, string SafeSummary);

/// <summary>Outcome of <c>session_open</c>.</summary>
public sealed record SessionOpenResult(
    bool Opened,
    IReadOnlyList<string> Scopes,
    string? ErrorCode,
    string? SafeSummary)
{
    public static SessionOpenResult Success(IReadOnlyList<string> scopes) =>
        new(true, scopes, null, null);

    public static SessionOpenResult Unavailable() =>
        new(false, [], "scope_unavailable", "scope is not executable");

    public static SessionOpenResult Conflict() =>
        new(false, [], "session_conflict", "session is already owned");
}

/// <summary>
/// Outcome of <c>run.open</c> / <c>run.policy.rebind</c>: either a lease view
/// or the stable error code and summary for the wire reply.
/// </summary>
public sealed record RunLeaseResult(
    RuntimeError? Error,
    string? RunId = null,
    long SnapshotVersion = 0,
    long BindingVersion = 0,
    IReadOnlyList<string>? AdvertisedCapabilities = null);

/// <summary>
/// Outcome of <c>run.close</c>. <see cref="Refusal"/> is the session-precondition
/// tool-error shape; otherwise the reply is a <c>run_closed</c> envelope whose
/// status is succeeded when <see cref="Closed"/>.
/// </summary>
public sealed record RunCloseResult(
    RuntimeError? Refusal,
    bool Closed,
    string RunId,
    string? ErrorCode = null,
    string? SafeSummary = null);

/// <summary>
/// The policy decision for one evaluated call. <see cref="MatchedRuleIdExplicit"/>
/// is set by the pre-decision refusals, whose wire payload always carries the
/// <c>matched_rule_id</c> field (even as null), unlike evaluator decisions.
/// </summary>
public sealed record ToolDecisionResult(
    string Status,
    string Decision,
    string ReasonCode,
    bool AnalyzerRiskPending,
    string SafeSummary,
    string? MatchedRuleId,
    bool MatchedRuleIdExplicit,
    string? AuthorizationToken = null,
    string? ApprovalId = null,
    string? ApprovalKind = null,
    string? BoundaryDirectory = null,
    string? ExpiresAt = null,
    JsonObject? MatcherPreview = null);

/// <summary>Outcome of <c>tool.evaluate</c>.</summary>
public sealed record ToolEvaluateResult(
    RuntimeError? Error,
    ToolDecisionResult? Decision);

/// <summary>
/// Outcome of a tool execution path: either a receipt (full wire payload) or a
/// pre-execution refusal that maps to the tool-error shape.
/// </summary>
public sealed record ToolExecutionReply(
    ToolResult? Receipt,
    string? ErrorCode,
    string? SafeSummary)
{
    public static ToolExecutionReply FromReceipt(ToolResult receipt) =>
        new(receipt, null, null);

    public static ToolExecutionReply Error(string errorCode, string safeSummary) =>
        new(null, errorCode, safeSummary);
}

/// <summary>
/// Outcome of <c>approval.respond</c>. <see cref="Refusal"/> is the session
/// precondition; otherwise the reply is an <c>approval_result</c> envelope.
/// </summary>
public sealed record ApprovalDecisionResult(
    RuntimeError? Refusal,
    string Status,
    string SafeSummary,
    string? ErrorCode = null,
    string? AuthorizationToken = null,
    string? ExpiresAt = null,
    JsonObject? MatcherPreview = null,
    ApprovalRememberPlan? RememberPlan = null)
{
    public static ApprovalDecisionResult Denied(string errorCode, string safeSummary) =>
        new(null, ToolResultStatuses.Denied, safeSummary, errorCode);

    public static ApprovalDecisionResult Authorized(
        string authorizationToken,
        string expiresAt,
        JsonObject matcherPreview,
        ApprovalRememberPlan? rememberPlan = null) =>
        new(null, "authorized", "approval authorized", AuthorizationToken: authorizationToken, ExpiresAt: expiresAt, MatcherPreview: matcherPreview, RememberPlan: rememberPlan);
}

/// <summary>
/// Outcome of <c>session.grants.revoke</c>. <see cref="Refusal"/> is the session
/// precondition; otherwise the reply is a <c>session_grants_revoked</c> envelope.
/// </summary>
public sealed record SessionGrantsRevokeResult(
    RuntimeError? Refusal,
    bool Revoked,
    string? ErrorCode,
    string SafeSummary);

/// <summary>
/// Outcome of <c>session.tasks</c>. <see cref="Refusal"/> is the session
/// precondition; otherwise the reply is a <c>session_tasks_result</c> envelope.
/// </summary>
public sealed record SessionTasksResult(
    RuntimeError? Refusal,
    string Status,
    string? ErrorCode,
    string SafeSummary,
    JsonObject? Payload)
{
    public static SessionTasksResult Success(JsonObject payload) =>
        new(null, ToolResultStatuses.Succeeded, null, "session tasks", payload);

    public static SessionTasksResult Failure(string errorCode, string safeSummary = "Session task control failed.") =>
        new(null, ToolResultStatuses.Failed, errorCode, safeSummary, null);

    public static SessionTasksResult Refused(RuntimeError error) =>
        new(error, ToolResultStatuses.Denied, error.ErrorCode, error.SafeSummary, null);
}
