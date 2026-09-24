namespace RuAgent.Harness.Core;

public sealed record HarnessEvent
{
    public HarnessEvent(
        EventId eventId,
        RunId runId,
        long sequence,
        string kind,
        DateTimeOffset occurredAt,
        PayloadId? payloadId = null,
        ModelStepId? modelStepId = null,
        ToolCallId? toolCallId = null,
        ExecutionId? executionId = null,
        ApprovalId? approvalId = null,
        int? safeCount = null,
        string? safeCode = null,
        ToolExecutionOutcome? toolOutcome = null,
        bool? sideEffectsPossible = null,
        string? providerCallId = null)
    {
        _ = eventId.Value;
        _ = runId.Value;
        if (payloadId is { } privatePayloadId)
        {
            _ = privatePayloadId.Value;
        }

        ValidateOptional(modelStepId, id => id.Value);
        ValidateOptional(toolCallId, id => id.Value);
        ValidateOptional(executionId, id => id.Value);
        ValidateOptional(approvalId, id => id.Value);

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (!IsSafeKind(kind))
        {
            throw new ArgumentException(
                "Event kind must be a bounded lowercase token.",
                nameof(kind));
        }

        if (safeCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(safeCount));
        }

        if (safeCode is not null && !IsSafeKind(safeCode))
        {
            throw new ArgumentException("Event safe code must be a bounded token.", nameof(safeCode));
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

        if (providerCallId is not null && !ProviderCallIdContract.IsValid(providerCallId))
        {
            throw new ArgumentException("Provider call identifier is invalid.", nameof(providerCallId));
        }

        EventId = eventId;
        RunId = runId;
        Sequence = sequence;
        Kind = kind;
        OccurredAt = occurredAt;
        PayloadId = payloadId;
        ModelStepId = modelStepId;
        ToolCallId = toolCallId;
        ProviderCallId = providerCallId;
        ExecutionId = executionId;
        ApprovalId = approvalId;
        SafeCount = safeCount;
        SafeCode = safeCode;
        ToolOutcome = toolOutcome;
        SideEffectsPossible = sideEffectsPossible;
    }

    public EventId EventId { get; }

    public RunId RunId { get; }

    public long Sequence { get; }

    public string Kind { get; }

    public DateTimeOffset OccurredAt { get; }

    public PayloadId? PayloadId { get; }

    public ModelStepId? ModelStepId { get; }

    public ToolCallId? ToolCallId { get; }

    public string? ProviderCallId { get; }

    public ExecutionId? ExecutionId { get; }

    public ApprovalId? ApprovalId { get; }

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

    private static bool IsSafeKind(string? value)
    {
        if (value is not { Length: >= 1 and <= 64 }
            || value[0] is not (>= 'a' and <= 'z'))
        {
            return false;
        }

        return value.All(character =>
            character is (>= 'a' and <= 'z')
                or (>= '0' and <= '9')
                or '_'
                or '.');
    }
}
