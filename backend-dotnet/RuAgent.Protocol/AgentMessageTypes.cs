namespace RuAgent.Protocol;

public static class AgentMessageTypes
{
    public const string Hello = "hello";
    public const string HelloOk = "hello_ok";
    public const string SessionOpen = "session_open";
    public const string SessionClose = "session_close";
    public const string SkillStart = "skill_start";
    public const string SkillEvent = "skill_event";
    public const string SkillResult = "skill_result";
    public const string ToolRequest = "tool_request";
    public const string ApprovalRequired = "approval_required";
    public const string ApprovalResponse = "approval_response";
    public const string ToolResult = "tool_result";
    public const string Cancel = "cancel";
    public const string Health = "health";
    public const string HealthOk = "health_ok";

    public static IReadOnlySet<string> Known { get; } = new HashSet<string>(
        [
            Hello,
            HelloOk,
            SessionOpen,
            SessionClose,
            SkillStart,
            SkillEvent,
            SkillResult,
            ToolRequest,
            ApprovalRequired,
            ApprovalResponse,
            ToolResult,
            Cancel,
            Health,
            HealthOk,
        ],
        StringComparer.Ordinal);
}
