using RuAgent.Protocol.Harness;

namespace RuAgent.Harness.Core;

public sealed record AgentRun
{
    public AgentRun(
        RunId runId,
        SessionId sessionId,
        WorkspaceId workspaceId,
        HarnessRunStatus status,
        long version,
        long lastSequence,
        int budgetWindow,
        int modelStepsInWindow,
        int toolCallsInWindow,
        AgentPermissionPreset preset,
        long policySnapshotVersion,
        string policySnapshotSha256,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? terminalAt = null,
        string? terminalErrorCode = null,
        int? permissionProfileVersion = null)
    {
        _ = runId.Value;
        _ = sessionId.Value;
        _ = workspaceId.Value;

        if (version < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        if (lastSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastSequence));
        }

        if (budgetWindow <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budgetWindow));
        }

        if (modelStepsInWindow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modelStepsInWindow));
        }

        if (toolCallsInWindow < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(toolCallsInWindow));
        }

        if (policySnapshotVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policySnapshotVersion));
        }

        if (!Enum.IsDefined(preset))
        {
            throw new ArgumentOutOfRangeException(nameof(preset));
        }

        if (permissionProfileVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(permissionProfileVersion));
        }

        if (!IsSha256(policySnapshotSha256))
        {
            throw new ArgumentException(
                "Policy snapshot hash must contain exactly 64 lowercase hexadecimal characters.",
                nameof(policySnapshotSha256));
        }

        if (updatedAt < createdAt)
        {
            throw new ArgumentException(
                "Updated timestamp cannot precede creation.",
                nameof(updatedAt));
        }

        ValidateTerminalMetadata(status, createdAt, updatedAt, terminalAt, terminalErrorCode);

        RunId = runId;
        SessionId = sessionId;
        WorkspaceId = workspaceId;
        Status = status;
        Version = version;
        LastSequence = lastSequence;
        BudgetWindow = budgetWindow;
        ModelStepsInWindow = modelStepsInWindow;
        ToolCallsInWindow = toolCallsInWindow;
        Preset = preset;
        PolicySnapshotVersion = policySnapshotVersion;
        PolicySnapshotSha256 = policySnapshotSha256;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        TerminalAt = terminalAt;
        TerminalErrorCode = terminalErrorCode;
        PermissionProfileVersion = permissionProfileVersion ?? AgentPermissionPresetMapper.CurrentPermissionProfileVersion;
    }

    public RunId RunId { get; }

    public SessionId SessionId { get; }

    public WorkspaceId WorkspaceId { get; }

    public HarnessRunStatus Status { get; }

    public HarnessRunState State => Status.State;

    public HistoryWaitKind? HistoryWaitKind => Status.HistoryWaitKind;

    public long Version { get; }

    public long LastSequence { get; }

    public int BudgetWindow { get; }

    public int ModelStepsInWindow { get; }

    public int ToolCallsInWindow { get; }

    public AgentPermissionPreset Preset { get; }

    public long PolicySnapshotVersion { get; }

    public string PolicySnapshotSha256 { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    public DateTimeOffset? TerminalAt { get; }

    public string? TerminalErrorCode { get; }

    public int PermissionProfileVersion { get; }

    internal AgentRun Advance(
        HarnessRunStatus nextStatus,
        int nextBudgetWindow,
        int nextModelStepsInWindow,
        int nextToolCallsInWindow,
        long appendedEventCount,
        DateTimeOffset updatedAt,
        string? terminalErrorCode = null)
    {
        if (appendedEventCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(appendedEventCount));
        }

        DateTimeOffset? terminalAt = nextStatus.State.IsTerminal() ? updatedAt : null;
        return new AgentRun(
            RunId,
            SessionId,
            WorkspaceId,
            nextStatus,
            checked(Version + 1),
            checked(LastSequence + appendedEventCount),
            nextBudgetWindow,
            nextModelStepsInWindow,
            nextToolCallsInWindow,
            Preset,
            PolicySnapshotVersion,
            PolicySnapshotSha256,
            CreatedAt,
            updatedAt,
            terminalAt,
            terminalErrorCode,
            PermissionProfileVersion);
    }

    private static void ValidateTerminalMetadata(
        HarnessRunStatus status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        DateTimeOffset? terminalAt,
        string? terminalErrorCode)
    {
        if (!status.State.IsTerminal())
        {
            if (terminalAt is not null || terminalErrorCode is not null)
            {
                throw new ArgumentException(
                    "A nonterminal run cannot contain terminal metadata.",
                    nameof(status));
            }

            return;
        }

        if (terminalAt is null || terminalAt < createdAt || terminalAt > updatedAt)
        {
            throw new ArgumentException(
                "A terminal run requires a terminal timestamp within its lifetime.",
                nameof(terminalAt));
        }

        if (status.State is HarnessRunState.Failed or HarnessRunState.Interrupted)
        {
            if (!IsErrorCode(terminalErrorCode))
            {
                throw new ArgumentException(
                    "Failed or interrupted runs require a stable terminal error code.",
                    nameof(terminalErrorCode));
            }

            return;
        }

        if (terminalErrorCode is not null
            && (status.State != HarnessRunState.Cancelled || !IsErrorCode(terminalErrorCode)))
        {
            throw new ArgumentException(
                "Only cancelled runs may carry an optional terminal reason code.",
                nameof(terminalErrorCode));
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 }
        && value.All(character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsErrorCode(string? value)
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
