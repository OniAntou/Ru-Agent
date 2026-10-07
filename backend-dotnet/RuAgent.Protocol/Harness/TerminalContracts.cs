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
    DirectDelete = 7,
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

    /// <summary>
    /// Concurrent background terminal tasks one session may keep alive. Past
    /// this the start fails closed with background_task_limit_reached.
    /// </summary>
    public const int MaxBackgroundTasksPerSession = 4;

    /// <summary>
    /// Background lifetime when the call omits timeout_ms.
    /// </summary>
    public const int BackgroundDefaultLifetimeMs = 30 * 60_000;

    /// <summary>
    /// Hard ceiling on a background task's lifetime. The lifetime cap replaces
    /// the standard-window approval gate for background calls because they do
    /// not block the turn.
    /// </summary>
    public const int BackgroundMaxLifetimeMs = 2 * 60 * 60_000;

    public const int MaxArguments = 64;
    public const int MaxArgumentChars = 1024;
    public const int MaxScriptChars = 32_000;
    public const int MaxExecutableChars = 2048;
    public const int MaxWorkingDirectoryChars = 2048;

    /// <summary>
    /// Full redacted output retained per terminal call for later paging.
    /// Matches the runner's total retention so the spool never promises more
    /// than execution kept.
    /// </summary>
    public const int MaxSpoolChars = 256 * 1024;

    /// <summary>
    /// Largest single page a spool read may return.
    /// </summary>
    public const int MaxSpoolPageChars = 64 * 1024;

    /// <summary>
    /// Longest spool identifier a read may name.
    /// </summary>
    public const int MaxExecutionIdChars = 256;
}
