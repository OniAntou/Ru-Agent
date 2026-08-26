namespace RuAgent.Harness.Core.Persistence;

public enum HarnessEventKind
{
    RunStateChanged,
    RunFailed,
    ModelStepStarted,
    ModelStepCompleted,
    ToolProposed,
    ToolExecutionRequested,
    ToolExecutionCompleted,
    ApprovalRequested,
    ContinueRequested,
    ReconciliationRequired,
    ReconciliationResolved,
    TransientFramesDropped,
}

public sealed record HarnessEventIntent
{
    public HarnessEventIntent(
        EventId eventId,
        HarnessEventKind kind,
        ModelStepId? modelStepId = null,
        ToolCallId? toolCallId = null,
        ExecutionId? executionId = null,
        ApprovalId? approvalId = null,
        PayloadId? payloadId = null,
        int? safeCount = null,
        string? safeCode = null,
        ToolExecutionOutcome? toolOutcome = null,
        bool? sideEffectsPossible = null)
    {
        _ = eventId.Value;
        ValidateOptional(modelStepId, id => id.Value);
        ValidateOptional(toolCallId, id => id.Value);
        ValidateOptional(executionId, id => id.Value);
        ValidateOptional(approvalId, id => id.Value);
        ValidateOptional(payloadId, id => id.Value);
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (safeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(safeCount));
        }

        if (safeCode is not null && !IsSafeCode(safeCode))
        {
            throw new ArgumentException("Safe code must be a bounded token.", nameof(safeCode));
        }

        if (toolOutcome is { } outcome && !Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(toolOutcome));
        }

        if ((toolOutcome is null) != (sideEffectsPossible is null))
        {
            throw new ArgumentException(
                "Tool outcome and side-effect flag must be present together.",
                nameof(toolOutcome));
        }

        EventId = eventId;
        Kind = kind;
        ModelStepId = modelStepId;
        ToolCallId = toolCallId;
        ExecutionId = executionId;
        ApprovalId = approvalId;
        PayloadId = payloadId;
        SafeCount = safeCount;
        SafeCode = safeCode;
        ToolOutcome = toolOutcome;
        SideEffectsPossible = sideEffectsPossible;
    }

    public EventId EventId { get; }

    public HarnessEventKind Kind { get; }

    public ModelStepId? ModelStepId { get; }

    public ToolCallId? ToolCallId { get; }

    public ExecutionId? ExecutionId { get; }

    public ApprovalId? ApprovalId { get; }

    public PayloadId? PayloadId { get; }

    public int? SafeCount { get; }

    public string? SafeCode { get; }

    public ToolExecutionOutcome? ToolOutcome { get; }

    public bool? SideEffectsPossible { get; }

    private static void ValidateOptional<T>(T? value, Func<T, string> readValue)
        where T : struct
    {
        if (value is { } present)
        {
            _ = readValue(present);
        }
    }

    private static bool IsSafeCode(string value) =>
        value is { Length: >= 1 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '_'
                or '.');
}
