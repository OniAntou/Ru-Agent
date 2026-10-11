using System.Collections.Frozen;

namespace RuAgent.Protocol;

public static class AgentMessageTypes
{
    public const string Hello = "hello";
    public const string HelloOk = "hello_ok";
    public const string SessionOpen = "session_open";
    public const string SessionOpened = "session_opened";
    public const string SessionClose = "session_close";
    public const string ToolRequest = "tool_request";
    public const string ApprovalRequired = "approval_required";
    public const string ApprovalResponse = "approval_response";
    public const string ToolResult = "tool_result";
    public const string Cancel = "cancel";
    public const string Health = "health";
    public const string HealthOk = "health_ok";
    public const string RunOpen = "run.open";
    public const string RunOpened = "run.opened";
    public const string RunPolicyRebind = "run.policy.rebind";
    public const string RunRebound = "run.policy.rebound";
    public const string RunClose = "run.close";
    public const string RunClosed = "run.closed";
    public const string ToolEvaluate = "tool.evaluate";
    public const string ToolDecision = "tool.decision";
    public const string ToolExecute = "tool.execute";
    public const string ToolExecuteResult = "tool.result";
    public const string ToolOutputDelta = "tool.output.delta";
    public const string ApprovalRespond = "approval.respond";
    public const string ApprovalResult = "approval.result";
    public const string SessionGrantsRevoke = "session.grants.revoke";
    public const string SessionGrantsRevoked = "session.grants.revoked";
    public const string SessionTasks = "session.tasks";
    public const string SessionTasksResult = "session.tasks.result";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(
        [
            Hello,
            HelloOk,
            SessionOpen,
            SessionOpened,
            SessionClose,
            ToolRequest,
            ApprovalRequired,
            ApprovalResponse,
            ToolResult,
            Cancel,
            Health,
            HealthOk,
            RunOpen,
            RunOpened,
            RunPolicyRebind,
            RunRebound,
            RunClose,
            RunClosed,
            ToolEvaluate,
            ToolDecision,
            ToolExecute,
            ToolExecuteResult,
            ToolOutputDelta,
            ApprovalRespond,
            ApprovalResult,
            SessionGrantsRevoke,
            SessionGrantsRevoked,
            SessionTasks,
            SessionTasksResult,
        ],
        StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> V2Only { get; } = new HashSet<string>(
        [
            RunOpen,
            RunOpened,
            RunPolicyRebind,
            RunRebound,
            RunClose,
            RunClosed,
            ToolEvaluate,
            ToolDecision,
            ToolExecute,
            ToolExecuteResult,
            ToolOutputDelta,
            ApprovalRespond,
            ApprovalResult,
            SessionGrantsRevoke,
            SessionGrantsRevoked,
            SessionTasks,
            SessionTasksResult,
        ],
        StringComparer.Ordinal).ToFrozenSet(StringComparer.Ordinal);
}
