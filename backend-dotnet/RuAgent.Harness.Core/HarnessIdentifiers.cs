namespace RuAgent.Harness.Core;

internal static class HarnessIdentifier
{
    internal static string ValidateGenerated(string? value, string prefix, string parameterName)
    {
        if (value is null
            || value.Length != prefix.Length + 32
            || !value.StartsWith(prefix, StringComparison.Ordinal)
            || !IsLowerHex(value.AsSpan(prefix.Length)))
        {
            throw new ArgumentException(
                $"Identifier must be '{prefix}' followed by 32 lowercase hexadecimal characters.",
                parameterName);
        }

        return value;
    }

    internal static string ValidateSession(string? value, string parameterName)
    {
        if (value is null || value.Length != 32 || !IsLowerHex(value.AsSpan()))
        {
            throw new ArgumentException(
                "Session identifier must contain exactly 32 lowercase hexadecimal characters.",
                parameterName);
        }

        return value;
    }

    internal static string RequireInitialized(string? value, string identifierName) =>
        value ?? throw new InvalidOperationException($"{identifierName} is uninitialized.");

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

public readonly record struct WorkspaceId
{
    public WorkspaceId(string value) =>
        _value = HarnessIdentifier.ValidateGenerated(value, "ws_", nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(WorkspaceId));
    public override string ToString() => Value;
}

public readonly record struct SessionId
{
    public SessionId(string value) =>
        _value = HarnessIdentifier.ValidateSession(value, nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(SessionId));
    public override string ToString() => Value;
}

public readonly record struct RunId
{
    public RunId(string value) =>
        _value = HarnessIdentifier.ValidateGenerated(value, "run_", nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(RunId));
    public override string ToString() => Value;
}

public readonly record struct ModelStepId
{
    public ModelStepId(string value) =>
        _value = HarnessIdentifier.ValidateGenerated(value, "step_", nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(ModelStepId));
    public override string ToString() => Value;
}

public readonly record struct ToolCallId
{
    public ToolCallId(string value) =>
        _value = HarnessIdentifier.ValidateGenerated(value, "call_", nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(ToolCallId));
    public override string ToString() => Value;
}

public readonly record struct ExecutionId
{
    public ExecutionId(string value) =>
        _value = HarnessIdentifier.ValidateGenerated(value, "exec_", nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(ExecutionId));
    public override string ToString() => Value;
}

public readonly record struct ApprovalId
{
    public ApprovalId(string value) =>
        _value = HarnessIdentifier.ValidateGenerated(value, "apr_", nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(ApprovalId));
    public override string ToString() => Value;
}

public readonly record struct TrustRequestId
{
    public TrustRequestId(string value) =>
        _value = HarnessIdentifier.ValidateGenerated(value, "trust_", nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(TrustRequestId));
    public override string ToString() => Value;
}

public readonly record struct PolicyRuleId
{
    public PolicyRuleId(string value) =>
        _value = HarnessIdentifier.ValidateGenerated(value, "rule_", nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(PolicyRuleId));
    public override string ToString() => Value;
}

public readonly record struct EventId
{
    public EventId(string value) =>
        _value = HarnessIdentifier.ValidateGenerated(value, "evt_", nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(EventId));
    public override string ToString() => Value;
}

public readonly record struct PayloadId
{
    public PayloadId(string value) =>
        _value = HarnessIdentifier.ValidateGenerated(value, "payload_", nameof(value));

    private readonly string? _value;
    public string Value => HarnessIdentifier.RequireInitialized(_value, nameof(PayloadId));
    public override string ToString() => Value;
}
