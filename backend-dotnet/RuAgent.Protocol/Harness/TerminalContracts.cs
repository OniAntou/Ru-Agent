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
    WorkspaceScript = 8,
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
    /// Default concurrent terminal tasks per session. The Broker may configure
    /// a value from 1 to 64; a full session fails closed with
    /// background_task_limit_reached.
    /// </summary>
    public const int MaxBackgroundTasksPerSession = 8;

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

    public const int MaxArguments = 128;
    public const int MaxArgumentChars = 4096;
    public const int MaxScriptChars = 32_000;
    public const int MaxExecutableChars = 2048;
    public const int MaxCanonicalExecutableChars = 4096;
    public const int MaxWorkingDirectoryChars = 2048;

    /// <summary>
    /// In-memory window retained per terminal call: the head of the full
    /// redacted output, kept for receipts and live paging. Output past this
    /// window spills to a session-scoped spool file up to
    /// <see cref="MaxSpoolFileBytes"/>.
    /// </summary>
    public const int MaxSpoolChars = 256 * 1024;

    /// <summary>
    /// Retention ceiling for one spilled spool file, counted in UTF-8 bytes.
    /// Past this the spool stops recording and the receipt names the
    /// truncation; the command itself keeps running to its own timeout or
    /// lifetime.
    /// </summary>
    public const int MaxSpoolFileBytes = 1024 * 1024 * 1024;

    /// <summary>
    /// The same retention ceiling counted in UTF-16 characters: spool reads
    /// page by character offset, so the read cursor bound uses this name while
    /// <see cref="MaxSpoolFileBytes"/> names the byte budget the writer
    /// enforces.
    /// </summary>
    public const int MaxSpoolFileChars = 1024 * 1024 * 1024;

    /// <summary>
    /// Aggregate UTF-8 byte budget for one session's spilled spool files. Past
    /// this the oldest spill is evicted and its file deleted, so a chatty
    /// session cannot grow the spool root without bound.
    /// </summary>
    public const long MaxSpoolSessionBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Largest single page a spool read may return.
    /// </summary>
    public const int MaxSpoolPageChars = 64 * 1024;

    /// <summary>
    /// Longest spool identifier a read may name.
    /// </summary>
    public const int MaxExecutionIdChars = 256;
}
