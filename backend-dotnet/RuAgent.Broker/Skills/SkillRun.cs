using System.Text.Json.Nodes;

namespace RuAgent.Broker.Skills;

public enum SkillRunState
{
    Selected,
    InputValidated,
    StepReady,
    ToolRequested,
    ApprovalRequired,
    ToolRunning,
    ResultSanitized,
    NextStep,
    Completed,
    Failed,
    Cancelled,
}

public sealed record SkillStepReceipt(
    int StepIndex,
    SkillStepKind Kind,
    string Name,
    string? ToolId,
    string Status,
    string SafeSummary);

public sealed record SkillRun(
    string RunId,
    string SessionId,
    string SkillId,
    SkillRunState State,
    int StepIndex,
    string SafeSummary,
    string? ErrorCode = null,
    JsonObject? Output = null,
    IReadOnlyList<SkillStepReceipt>? Steps = null,
    string? ReceiptId = null)
{
    public IReadOnlyList<SkillStepReceipt> StepReceipts { get; } = Steps ?? Array.Empty<SkillStepReceipt>();

    public string Receipt => ReceiptId ?? RunId;
}
