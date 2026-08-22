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

public sealed record SkillRun(
    string RunId,
    string SessionId,
    string SkillId,
    SkillRunState State,
    int StepIndex,
    string SafeSummary);
