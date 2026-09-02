using System.Text.Json.Serialization;

namespace RuAgent.Protocol;

/// <summary>
/// Recognized command classes assigned by versioned analyzers; Unknown always asks in Standard.
/// </summary>
public enum TerminalOperationClass
{
    Unknown = 0,
    ReadOnly = 1,
    Test = 2,
    Build = 3,
    Lint = 4,
    GitMutation = 5,
    ExternalMutation = 6,
}

/// <summary>
/// Derived from the requested timeout: at or below the standard approval threshold the command
/// decision stands; above it an explicit approval is required before execution. The hard maximum
/// is absolute and rejects before any adapter work.
/// </summary>
public enum TerminalTimeoutClass
{
    StandardWindow = 0,
    ApprovalRequired = 1,
}

public static class TerminalLimits
{
    public const int DefaultTimeoutMs = 120_000;
    public const int HardMaxTimeoutMs = 1_800_000;
    public const int StandardApprovalThresholdMs = 900_000;
    public const int MaxArguments = 64;
    public const int MaxArgumentChars = 1024;
    public const int MaxScriptChars = 16_000;
    public const int MaxExecutableChars = 2048;
    public const int MaxWorkingDirectoryChars = 2048;
}

public sealed record TerminalWriteSetInput(
    [property: JsonPropertyName("read_only")] bool ReadOnly,
    [property: JsonPropertyName("write_set")] IReadOnlyList<string> Paths)
{
    public void Validate() => Harness.WorkspaceWriteSet.Validate(ReadOnly, Paths);
}
