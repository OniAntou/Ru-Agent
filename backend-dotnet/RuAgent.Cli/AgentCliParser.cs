using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Cli;

public enum CliCommandKind
{
    Run,
    Attach,
    Trust,
    Cancel,
    Continue,
    Status,
    Approve,
    Deny,
    Answer,
    PolicyList,
    PolicyImport,
    PolicyRemove,
    Reconcile,
    Output,
    Command,
    Chat,
    SessionList,
    SessionHistory,
    SessionSearch,
    SessionRewind,
    SessionDelete,
    SessionExport,
    SessionImport,
    Rpc,
}

public enum ApprovalScope
{
    Once,
    Run,
    Project,
}

public abstract record AgentCommand(CliCommandKind Kind, bool Json);

public sealed record RunCommand(
    string WorkspacePath,
    string Prompt,
    string? SessionId,
    string Preset,
    bool Json,
    bool TrustWorkspace = false,
    bool ConfirmFullAccess = false,
    IReadOnlyList<string>? Images = null) : AgentCommand(CliCommandKind.Run, Json);

public sealed record AttachCommand(
    string RunId,
    bool Json) : AgentCommand(CliCommandKind.Attach, Json);

public sealed record TrustCommand(
    string RunId,
    bool Approve) : AgentCommand(CliCommandKind.Trust, false);

public sealed record CancelCommand(
    string RunId) : AgentCommand(CliCommandKind.Cancel, false);

public sealed record ContinueCommand(
    string RunId) : AgentCommand(CliCommandKind.Continue, false);

public sealed record StatusCommand(
    string RunId,
    bool Json) : AgentCommand(CliCommandKind.Status, Json);

public sealed record ApproveCommand(
    string RunId,
    string ApprovalId,
    ApprovalScope Scope,
    bool Json,
    bool Always = false) : AgentCommand(CliCommandKind.Approve, Json);

public sealed record DenyCommand(
    string RunId,
    string ApprovalId,
    bool Json) : AgentCommand(CliCommandKind.Deny, Json);

/// <summary>
/// `agent answer <run-id> <input-id> [answer] [--json]`. Without the answer
/// argument the command asks the terminal operator; --json requires the argument
/// because it must never prompt.
/// </summary>
public sealed record AnswerCommand(
    string RunId,
    string InputId,
    string? Answer,
    bool Json) : AgentCommand(CliCommandKind.Answer, Json);

public sealed record PolicyListCommand(
    bool Global,
    string? WorkspacePath,
    bool Json) : AgentCommand(CliCommandKind.PolicyList, Json);

public sealed record PolicyImportCommand(
    bool Global,
    string FilePath,
    bool Json) : AgentCommand(CliCommandKind.PolicyImport, Json);

public sealed record PolicyRemoveCommand(
    string RuleId,
    bool Json) : AgentCommand(CliCommandKind.PolicyRemove, Json);

/// <summary>
/// `agent reconcile <run-id> [--outcome applied|not-applied|unknown] [--hash SHA256]
/// [--json]`. Without --outcome the command only fetches the reconciliation info.
/// </summary>
public sealed record ReconcileCommand(
    string RunId,
    string? OutcomeToken,
    string? SnapshotHash,
    bool Json) : AgentCommand(CliCommandKind.Reconcile, Json)
{
    public string ToWireOutcome() => OutcomeToken switch
    {
        "applied" => "applied",
        "not-applied" => "not_applied",
        "unknown" => "unknown_continue",
        _ => throw new InvalidOperationException("invalid_outcome"),
    };
}

/// <summary>
/// `agent output <run-id> <payload-id> [--json]` — the explicit user action that
/// fetches final head/tail tool output bytes; nothing fetches them implicitly.
/// </summary>
public sealed record OutputCommand(
    string RunId,
    string PayloadId,
    bool Json) : AgentCommand(CliCommandKind.Output, Json);

public sealed record DispatchCommandCommand(
    string RunId,
    string Text,
    bool Json) : AgentCommand(CliCommandKind.Command, Json);

public sealed record ChatCommand(
    string WorkspacePath,
    string? SessionId,
    string Preset,
    bool Json,
    bool Classic = false,
    bool TrustWorkspace = false,
    bool ConfirmFullAccess = false,
    IReadOnlyList<string>? Images = null,
    bool WorkspaceExplicit = true) : AgentCommand(CliCommandKind.Chat, Json);

public sealed record SessionListCommand(
    bool Json,
    string? Cursor = null,
    int Limit = 100,
    string? Query = null) : AgentCommand(CliCommandKind.SessionList, Json);

public sealed record SessionSearchCommand(
    string Query,
    int Limit,
    bool Json) : AgentCommand(CliCommandKind.SessionSearch, Json);

public sealed record SessionHistoryCommand(
    string SessionId,
    string? Cursor,
    int Limit,
    bool Json) : AgentCommand(CliCommandKind.SessionHistory, Json);

public sealed record SessionRewindCommand(
    string SessionId,
    int ToMessageIndex,
    bool Json) : AgentCommand(CliCommandKind.SessionRewind, Json);

/// <summary>
/// `agent session delete <session-id> [--yes]`. Without <c>--yes</c> the command asks the
/// terminal operator to confirm; the flag is the explicit non-interactive consent.
/// </summary>
public sealed record SessionDeleteCommand(
    string SessionId,
    bool Yes) : AgentCommand(CliCommandKind.SessionDelete, Json: false);

/// <summary>Writes one of Ru's sessions out in another agent's format.</summary>
public sealed record SessionExportCommand(
    string SessionId,
    string Harness,
    string? OutputDirectory,
    bool Json) : AgentCommand(CliCommandKind.SessionExport, Json);

/// <summary>Takes another agent's session file and makes it a Ru session.</summary>
/// <param name="SessionId">
/// Which session to take from a store that holds many. Only opencode needs it: its sessions
/// live in one database rather than one file each.
/// </param>
public sealed record SessionImportCommand(
    string FilePath,
    string? Harness,
    string? SessionId,
    bool Json) : AgentCommand(CliCommandKind.SessionImport, Json);

public sealed record RpcCommand() : AgentCommand(CliCommandKind.Rpc, Json: false);

public sealed record AgentCliParseResult(
    bool Success,
    AgentCommand? Command,
    string? ErrorCode,
    string? ErrorMessage);

public static partial class AgentCliParser
{
    public const int MaxArgCount = 64;
    public const int MaxWorkspacePathLength = 512;
    public const int MaxPromptLength = 8192;
    public const int MaxSessionIdLength = 64;
    public const int MaxRunIdLength = 36;
    public const int MaxPresetLength = 16;
    public const int MaxApprovalIdLength = 36;
    public const int MaxInputIdLength = 36;
    public const int MaxRuleIdLength = 37;
    public const int MaxFilePathLength = 4096;
    public const int MaxHarnessLength = 32;

    private static readonly Regex RunIdRegex = new(@"^run_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex ApprovalIdRegex = new(@"^apr_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex InputIdRegex = new(@"^inp_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex RuleIdRegex = new(@"^rule_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex SessionIdLooseRegex = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);

    public static AgentCliParseResult Parse(string[] args)
    {
        if (args is null)
            return Fail("invalid_arguments", "Arguments null.");

        if (args.Length > MaxArgCount)
            return Fail("too_many_arguments", $"Argument count {args.Length} exceeds maximum {MaxArgCount}.");

        var maxArgumentBytes = args.Length > 1 && string.Equals(args[1], "command", StringComparison.Ordinal)
            ? 16 * 1024
            : MaxPromptLength;
        foreach (var a in args)
        {
            if (a is not null && Encoding.UTF8.GetByteCount(a) > maxArgumentBytes)
            {
                return Fail("argument_too_long", $"Argument too long ({a.Length}).");
            }
        }

        if (args.Length == 0)
            return Fail("missing_command", "Expected 'agent' subcommand.");

        if (string.Equals(args[0], "chat", StringComparison.Ordinal) || string.Equals(args[0], "repl", StringComparison.Ordinal))
        {
            return ParseChat(args, 1);
        }

        if (string.Equals(args[0], "session", StringComparison.Ordinal))
        {
            return ParseSession(args, 1);
        }

        if (!string.Equals(args[0], "agent", StringComparison.Ordinal))
            return Fail("unknown_command", $"Unknown command '{args[0]}'. Expected 'agent'.");

        if (args.Length < 2)
            return Fail("missing_subcommand", "Missing agent subcommand. Expected run|attach|trust|cancel|continue|status|approve|deny|answer|policy|chat|session.");

        var sub = args[1];
        return sub switch
        {
            "run" => ParseRun(args),
            "attach" => ParseAttach(args),
            "trust" => ParseTrust(args),
            "cancel" => ParseCancel(args),
            "continue" => ParseContinue(args),
            "status" => ParseStatus(args),
            "approve" => ParseApprove(args),
            "deny" => ParseDeny(args),
            "answer" => ParseAnswer(args),
            "policy" => ParsePolicy(args),
            "reconcile" => ParseReconcile(args),
            "output" => ParseOutput(args),
            "command" => ParseDispatchCommand(args),
            "chat" or "repl" => ParseChat(args, 2),
            "session" => ParseSession(args, 2),
            "rpc" => ParseRpc(args),
            _ => Fail("unknown_subcommand", $"Unknown agent subcommand '{sub}'.")
        };
    }

    private static AgentCliParseResult? ValidateRunId(string runId)
    {
        if (runId.Length != MaxRunIdLength)
            return Fail("invalid_run_id", $"Run id must be length {MaxRunIdLength} (run_ + 32 hex). Got {runId.Length}.");
        if (!RunIdRegex.IsMatch(runId))
            return Fail("invalid_run_id", $"Run id must match run_[0-9a-f]{{32}}. Got '{runId}'.");
        return null;
    }

    private static AgentCliParseResult Fail(string code, string message) =>
        new(false, null, code, message);

    private static AgentCliParseResult Success(AgentCommand cmd) =>
        new(true, cmd, null, null);
}
