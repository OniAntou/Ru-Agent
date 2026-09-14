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
    PolicyList,
    PolicyImport,
    PolicyRemove,
    Reconcile,
    Output,
    Command,
    Chat,
    SessionList,
    SessionFork,
    SessionRewind,
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
    bool Json) : AgentCommand(CliCommandKind.Run, Json);

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
    bool Json) : AgentCommand(CliCommandKind.Approve, Json);

public sealed record DenyCommand(
    string RunId,
    string ApprovalId,
    bool Json) : AgentCommand(CliCommandKind.Deny, Json);

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
    bool Classic = false) : AgentCommand(CliCommandKind.Chat, Json);

public sealed record SessionListCommand(
    bool Json) : AgentCommand(CliCommandKind.SessionList, Json);

public sealed record SessionForkCommand(
    string SessionId,
    int UpToMessageIndex,
    string? NewTitle,
    bool Json) : AgentCommand(CliCommandKind.SessionFork, Json);

public sealed record SessionRewindCommand(
    string SessionId,
    int ToMessageIndex,
    bool Json) : AgentCommand(CliCommandKind.SessionRewind, Json);

public sealed record AgentCliParseResult(
    bool Success,
    AgentCommand? Command,
    string? ErrorCode,
    string? ErrorMessage);

public static class AgentCliParser
{
    public const int MaxArgCount = 64;
    public const int MaxWorkspacePathLength = 512;
    public const int MaxPromptLength = 8192;
    public const int MaxSessionIdLength = 64;
    public const int MaxRunIdLength = 36;
    public const int MaxPresetLength = 16;
    public const int MaxApprovalIdLength = 128;
    public const int MaxRuleIdLength = 37;
    public const int MaxFilePathLength = 4096;

    private static readonly Regex RunIdRegex = new(@"^run_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex ApprovalIdRegex = new(@"^apr_[0-9a-f]{32}$", RegexOptions.Compiled);
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
            return Fail("missing_subcommand", "Missing agent subcommand. Expected run|attach|trust|cancel|continue|status|approve|deny|policy|chat|session.");

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
            "policy" => ParsePolicy(args),
            "reconcile" => ParseReconcile(args),
            "output" => ParseOutput(args),
            "command" => ParseDispatchCommand(args),
            "chat" or "repl" => ParseChat(args, 2),
            "session" => ParseSession(args, 2),
            _ => Fail("unknown_subcommand", $"Unknown agent subcommand '{sub}'.")
        };
    }

    private static AgentCliParseResult ParseDispatchCommand(string[] args)
    {
        // agent command <run-id> <command-text> [--json]
        bool json = false;
        bool jsonSeen = false;
        string? runId = null;
        string? text = null;

        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }

            if (runId is null)
            {
                var validation = ValidateRunId(token);
                if (validation is not null) return validation;
                runId = token;
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent command'.");
            if (text is not null)
                return Fail("too_many_arguments", "Command text must be one argument; quote it when it contains spaces.");
            if (token.Length == 0)
                return Fail("missing_command_text", "Missing command text for 'agent command'.");
            if (token.Length > 16 * 1024)
                return Fail("command_too_long", "Command text is too long.");
            text = token;
        }

        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent command'.");
        if (text is null)
            return Fail("missing_command_text", "Missing required <command-text> for 'agent command'.");

        return Success(new DispatchCommandCommand(runId, text, json));
    }

    private static AgentCliParseResult ParseReconcile(string[] args)
    {
        // agent reconcile <run-id> [--outcome applied|not-applied|unknown] [--hash SHA256] [--json]
        bool json = false;
        bool jsonSeen = false;
        bool outcomeSeen = false;
        string? outcome = null;
        bool hashSeen = false;
        string? hash = null;
        string? runId = null;

        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--outcome")
            {
                if (outcomeSeen)
                    return Fail("duplicate_switch", "Duplicate --outcome.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --outcome.");
                var val = args[i + 1];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --outcome.");
                if (val != "applied" && val != "not-applied" && val != "unknown")
                    return Fail("invalid_outcome", $"Unknown outcome '{val}'. Expected applied|not-applied|unknown.");
                outcome = val;
                outcomeSeen = true;
                i++;
                continue;
            }
            if (token == "--hash")
            {
                if (hashSeen)
                    return Fail("duplicate_switch", "Duplicate --hash.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --hash.");
                var val = args[i + 1];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --hash.");
                if (val.Length != 64 || !val.All(char.IsAsciiHexDigit))
                    return Fail("invalid_snapshot_hash", "Snapshot hash must be a 64-character hex digest.");
                hash = val;
                hashSeen = true;
                i++;
                continue;
            }
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent reconcile'.");
            if (runId is not null)
                return Fail("too_many_arguments", "Too many positional arguments for 'agent reconcile'.");
            var validation = ValidateRunId(token);
            if (validation is not null) return validation;
            runId = token;
        }

        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent reconcile'.");

        return Success(new ReconcileCommand(runId, outcome, hash, json));
    }

    private static AgentCliParseResult ParseOutput(string[] args)
    {
        // agent output <run-id> <payload-id> [--json]
        bool json = false;
        bool jsonSeen = false;
        string? runId = null;
        string? payloadId = null;

        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent output'.");
            if (runId is null)
            {
                var validation = ValidateRunId(token);
                if (validation is not null) return validation;
                runId = token;
                continue;
            }
            if (payloadId is null)
            {
                if (token.Length == 0 || token.Length > 64 || !token.StartsWith("payload_", StringComparison.Ordinal))
                    return Fail("invalid_payload_id", "Payload id must start with 'payload_'.");
                payloadId = token;
                continue;
            }
            return Fail("too_many_arguments", "Too many positional arguments for 'agent output'.");
        }

        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent output'.");
        if (payloadId is null)
            return Fail("missing_payload_id", "Missing required <payload-id> for 'agent output'.");

        return Success(new OutputCommand(runId, payloadId, json));
    }

    private static AgentCliParseResult ParseRun(string[] args)
    {
        string? workspace = null;
        bool workspaceSeen = false;
        string preset = "standard";
        bool presetSeen = false;
        string? sessionId = null;
        bool sessionSeen = false;
        bool json = false;
        bool jsonSeen = false;
        string? prompt = null;

        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--workspace")
            {
                if (workspaceSeen)
                    return Fail("duplicate_switch", "Duplicate --workspace.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --workspace.");
                var val = args[i + 1];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --workspace.");
                if (val.Length == 0 || val.Length > MaxWorkspacePathLength)
                    return Fail("invalid_workspace", $"Workspace path length must be 1..{MaxWorkspacePathLength}.");
                if (val.Contains('\0'))
                    return Fail("invalid_workspace", "Workspace path contains null.");
                workspace = val;
                workspaceSeen = true;
                i++;
                continue;
            }
            if (token == "--preset")
            {
                if (presetSeen)
                    return Fail("duplicate_switch", "Duplicate --preset.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --preset.");
                var val = args[i + 1];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --preset.");
                if (val.Length == 0 || val.Length > MaxPresetLength)
                    return Fail("invalid_preset", "Preset too long.");
                if (val == "full")
                    val = "full_access";
                if (val != "standard" && val != "full_access" && val != "trusted")
                    return Fail("invalid_preset", $"Unknown preset '{val}'. Expected 'standard', 'full_access', or 'trusted'.");
                preset = val;
                presetSeen = true;
                i++;
                continue;
            }
            if (token == "--session")
            {
                if (sessionSeen)
                    return Fail("duplicate_switch", "Duplicate --session.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --session.");
                var val = args[i + 1];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --session.");
                if (val.Length == 0 || val.Length > MaxSessionIdLength)
                    return Fail("invalid_session_id", $"Session id length must be 1..{MaxSessionIdLength}.");
                if (!SessionIdLooseRegex.IsMatch(val))
                    return Fail("invalid_session_id", $"Session id contains invalid characters.");
                sessionId = val;
                sessionSeen = true;
                i++;
                continue;
            }
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent run'.");
            }
            if (prompt is not null)
                return Fail("too_many_arguments", "Multiple prompt arguments. Expected single prompt.");
            if (token.Length == 0)
                return Fail("missing_prompt", "Prompt must be non-empty.");
            if (token.Length > MaxPromptLength)
                return Fail("prompt_too_long", $"Prompt length {token.Length} exceeds maximum {MaxPromptLength}.");
            prompt = token;
        }

        if (!workspaceSeen || workspace is null)
            return Fail("missing_workspace", "Missing required --workspace for 'agent run'.");

        if (prompt is null)
            return Fail("missing_prompt", "Missing required prompt argument for 'agent run'.");

        if (preset == "full_access" && json)
            return Fail("interactive_required", "--json cannot be used with --preset full_access; interactive confirmation required.");
        if (preset == "trusted" && json)
            return Fail("interactive_required", "--json cannot be used with --preset trusted; interactive confirmation required.");

        return Success(new RunCommand(workspace, prompt, sessionId, preset, json));
    }

    private static AgentCliParseResult ParseAttach(string[] args)
    {
        bool json = false;
        bool jsonSeen = false;
        string? runId = null;

        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent attach'.");
            if (runId is not null)
                return Fail("too_many_arguments", "Too many positional arguments for 'agent attach'. Expected single <run-id>.");
            var validation = ValidateRunId(token);
            if (validation is not null) return validation;
            runId = token;
        }

        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent attach'.");

        return Success(new AttachCommand(runId, json));
    }

    private static AgentCliParseResult ParseTrust(string[] args)
    {
        bool approveSeen = false;
        bool denySeen = false;
        string? runId = null;

        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--approve")
            {
                if (approveSeen)
                    return Fail("duplicate_switch", "Duplicate --approve.");
                if (denySeen)
                    return Fail("duplicate_switch", "Cannot specify both --approve and --deny (mutually exclusive).");
                approveSeen = true;
                continue;
            }
            if (token == "--deny")
            {
                if (denySeen)
                    return Fail("duplicate_switch", "Duplicate --deny.");
                if (approveSeen)
                    return Fail("duplicate_switch", "Cannot specify both --approve and --deny (mutually exclusive).");
                denySeen = true;
                continue;
            }
            if (token == "--json")
            {
                return Fail("interactive_required", "--json cannot confirm workspace trust; interactive required.");
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent trust'.");
            if (runId is not null)
                return Fail("too_many_arguments", "Too many positional arguments for 'agent trust'.");
            var validation = ValidateRunId(token);
            if (validation is not null) return validation;
            runId = token;
        }

        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent trust'.");
        if (!approveSeen && !denySeen)
            return Fail("missing_decision", "Missing required --approve or --deny for 'agent trust'.");
        bool approve = approveSeen;
        return Success(new TrustCommand(runId, approve));
    }

    private static AgentCliParseResult ParseApprove(string[] args)
    {
        // agent approve <run-id> <approval-id> [--json]
        bool json = false;
        bool jsonSeen = false;
        string? runId = null;
        string? approvalId = null;

        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token == "--scope")
            {
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --scope.");
                return Fail("invalid_scope", "agent approve no longer takes --scope; the server selects the token from the pending binding.");
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent approve'.");
            if (runId is null)
            {
                var validation = ValidateRunId(token);
                if (validation is not null) return validation;
                runId = token;
                continue;
            }
            if (approvalId is null)
            {
                var validation = ValidateApprovalId(token);
                if (validation is not null) return validation;
                approvalId = token;
                continue;
            }
            return Fail("too_many_arguments", "Too many positional arguments for 'agent approve'.");
        }

        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent approve'.");
        if (approvalId is null)
            return Fail("missing_approval_id", "Missing required <approval-id> for 'agent approve'.");

        if (json)
            return Fail("interactive_required", "--json cannot confirm approval; interactive required.");

        return Success(new ApproveCommand(runId, approvalId, ApprovalScope.Once, json));
    }

    private static AgentCliParseResult ParseDeny(string[] args)
    {
        // agent deny <run-id> <approval-id> [--json]
        bool json = false;
        bool jsonSeen = false;
        string? runId = null;
        string? approvalId = null;

        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent deny'.");
            if (runId is null)
            {
                var validation = ValidateRunId(token);
                if (validation is not null) return validation;
                runId = token;
                continue;
            }
            if (approvalId is null)
            {
                var validation = ValidateApprovalId(token);
                if (validation is not null) return validation;
                approvalId = token;
                continue;
            }
            return Fail("too_many_arguments", "Too many positional arguments for 'agent deny'.");
        }

        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent deny'.");
        if (approvalId is null)
            return Fail("missing_approval_id", "Missing required <approval-id> for 'agent deny'.");

        if (json)
            return Fail("interactive_required", "--json cannot confirm approval denial; interactive required.");

        return Success(new DenyCommand(runId, approvalId, json));
    }

    private static AgentCliParseResult ParsePolicy(string[] args)
    {
        // agent policy <subcommand> ...
        if (args.Length < 3)
            return Fail("missing_policy_subcommand", "Missing policy subcommand. Expected list|import|remove.");

        var sub = args[2];
        return sub switch
        {
            "list" => ParsePolicyList(args),
            "import" => ParsePolicyImport(args),
            "remove" => ParsePolicyRemove(args),
            _ => Fail("unknown_subcommand", $"Unknown policy subcommand '{sub}'.")
        };
    }

    private static AgentCliParseResult ParsePolicyList(string[] args)
    {
        // agent policy list [--global | --workspace PATH] [--json]
        bool global = false;
        bool globalSeen = false;
        string? workspace = null;
        bool workspaceSeen = false;
        bool json = false;
        bool jsonSeen = false;

        for (int i = 3; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--global")
            {
                if (globalSeen)
                    return Fail("duplicate_switch", "Duplicate --global.");
                if (workspaceSeen)
                    return Fail("duplicate_switch", "Cannot specify both --global and --workspace.");
                global = true;
                globalSeen = true;
                continue;
            }
            if (token == "--workspace")
            {
                if (workspaceSeen)
                    return Fail("duplicate_switch", "Duplicate --workspace.");
                if (globalSeen)
                    return Fail("duplicate_switch", "Cannot specify both --global and --workspace.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --workspace.");
                var val = args[i + 1];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --workspace.");
                if (val.Length == 0 || val.Length > MaxWorkspacePathLength)
                    return Fail("invalid_workspace", $"Workspace path length must be 1..{MaxWorkspacePathLength}.");
                workspace = val;
                workspaceSeen = true;
                i++;
                continue;
            }
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent policy list'.");
            return Fail("too_many_arguments", $"Unexpected argument '{token}' for 'agent policy list'.");
        }

        return Success(new PolicyListCommand(global, workspace, json));
    }

    private static AgentCliParseResult ParsePolicyImport(string[] args)
    {
        // agent policy import --global FILE [--json]
        bool global = false;
        bool globalSeen = false;
        string? filePath = null;
        bool json = false;
        bool jsonSeen = false;

        for (int i = 3; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--global")
            {
                if (globalSeen)
                    return Fail("duplicate_switch", "Duplicate --global.");
                global = true;
                globalSeen = true;
                continue;
            }
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent policy import'.");
            if (filePath is not null)
                return Fail("too_many_arguments", "Multiple file arguments for 'agent policy import'. Expected single FILE.");
            if (token.Length == 0 || token.Length > MaxFilePathLength)
                return Fail("invalid_file_path", "File path invalid.");
            filePath = token;
        }

        if (!global)
            return Fail("missing_scope", "Missing required --global for 'agent policy import' (only global import supported).");
        if (filePath is null)
            return Fail("missing_file", "Missing required FILE for 'agent policy import'.");

        if (json)
            return Fail("interactive_required", "--json cannot confirm global policy import; interactive required.");

        return Success(new PolicyImportCommand(global, filePath, json));
    }

    private static AgentCliParseResult ParsePolicyRemove(string[] args)
    {
        // agent policy remove <rule-id> [--json]
        bool json = false;
        bool jsonSeen = false;
        string? ruleId = null;

        for (int i = 3; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent policy remove'.");
            if (ruleId is not null)
                return Fail("too_many_arguments", "Too many positional arguments for 'agent policy remove'.");
            var validation = ValidateRuleId(token);
            if (validation is not null) return validation;
            ruleId = token;
        }

        if (ruleId is null)
            return Fail("missing_rule_id", "Missing required <rule-id> for 'agent policy remove'.");

        if (json)
            return Fail("interactive_required", "--json cannot confirm policy removal interactively; use without --json.");

        return Success(new PolicyRemoveCommand(ruleId, json));
    }

    private static AgentCliParseResult ParseCancel(string[] args)
    {
        string? runId = null;
        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent cancel' (no switches allowed).");
            if (runId is not null)
                return Fail("too_many_arguments", "Too many positional arguments for 'agent cancel'.");
            var validation = ValidateRunId(token);
            if (validation is not null) return validation;
            runId = token;
        }
        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent cancel'.");
        return Success(new CancelCommand(runId));
    }

    private static AgentCliParseResult ParseContinue(string[] args)
    {
        string? runId = null;
        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent continue' (no switches allowed).");
            if (runId is not null)
                return Fail("too_many_arguments", "Too many positional arguments for 'agent continue'.");
            var validation = ValidateRunId(token);
            if (validation is not null) return validation;
            runId = token;
        }
        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent continue'.");
        return Success(new ContinueCommand(runId));
    }

    private static AgentCliParseResult ParseStatus(string[] args)
    {
        bool json = false;
        bool jsonSeen = false;
        string? runId = null;

        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent status'.");
            if (runId is not null)
                return Fail("too_many_arguments", "Too many positional arguments for 'agent status'.");
            var validation = ValidateRunId(token);
            if (validation is not null) return validation;
            runId = token;
        }

        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent status'.");

        return Success(new StatusCommand(runId, json));
    }

    private static AgentCliParseResult? ValidateRunId(string runId)
    {
        if (runId.Length != MaxRunIdLength)
            return Fail("invalid_run_id", $"Run id must be length {MaxRunIdLength} (run_ + 32 hex). Got {runId.Length}.");
        if (!RunIdRegex.IsMatch(runId))
            return Fail("invalid_run_id", $"Run id must match run_[0-9a-f]{{32}}. Got '{runId}'.");
        return null;
    }

    private static AgentCliParseResult? ValidateApprovalId(string approvalId)
    {
        if (approvalId.Length > MaxApprovalIdLength || approvalId.Length < 4)
            return Fail("invalid_approval_id", $"Approval id length must be 1..{MaxApprovalIdLength}.");
        if (!ApprovalIdRegex.IsMatch(approvalId) && !approvalId.StartsWith("apr_", StringComparison.Ordinal))
            return Fail("invalid_approval_id", $"Approval id must match apr_[0-9a-f]{{32}} or be apr_ prefixed. Got '{approvalId}'.");
        return null;
    }

    private static AgentCliParseResult? ValidateRuleId(string ruleId)
    {
        if (ruleId.Length != MaxRuleIdLength)
            return Fail("invalid_rule_id", $"Rule id must be length {MaxRuleIdLength} (rule_ + 32 hex). Got {ruleId.Length}.");
        if (!RuleIdRegex.IsMatch(ruleId))
            return Fail("invalid_rule_id", $"Rule id must match rule_[0-9a-f]{{32}}. Got '{ruleId}'.");
        return null;
    }

    private static AgentCliParseResult ParseChat(string[] args, int startIndex)
    {
        string? workspace = null;
        bool workspaceSeen = false;
        string preset = "standard";
        bool presetSeen = false;
        string? sessionId = null;
        bool sessionSeen = false;
        bool json = false;
        bool jsonSeen = false;
        bool classic = false;
        bool classicSeen = false;

        for (int i = startIndex; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--workspace")
            {
                if (workspaceSeen)
                    return Fail("duplicate_switch", "Duplicate --workspace.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --workspace.");
                var val = args[i + 1];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --workspace.");
                if (val.Length == 0 || val.Length > MaxWorkspacePathLength)
                    return Fail("invalid_workspace", $"Workspace path length must be 1..{MaxWorkspacePathLength}.");
                if (val.Contains('\0'))
                    return Fail("invalid_workspace", "Workspace path contains null.");
                workspace = val;
                workspaceSeen = true;
                i++;
                continue;
            }
            if (token == "--preset")
            {
                if (presetSeen)
                    return Fail("duplicate_switch", "Duplicate --preset.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --preset.");
                var val = args[i + 1];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --preset.");
                if (val.Length == 0 || val.Length > MaxPresetLength)
                    return Fail("invalid_preset", "Preset too long.");
                if (val == "full")
                    val = "full_access";
                if (val != "standard" && val != "full_access" && val != "trusted")
                    return Fail("invalid_preset", $"Unknown preset '{val}'. Expected 'standard', 'full_access', or 'trusted'.");
                preset = val;
                presetSeen = true;
                i++;
                continue;
            }
            if (token == "--session")
            {
                if (sessionSeen)
                    return Fail("duplicate_switch", "Duplicate --session.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --session.");
                var val = args[i + 1];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --session.");
                if (val.Length == 0 || val.Length > MaxSessionIdLength)
                    return Fail("invalid_session_id", $"Session id length must be 1..{MaxSessionIdLength}.");
                if (!SessionIdLooseRegex.IsMatch(val))
                    return Fail("invalid_session_id", "Session id contains invalid characters.");
                sessionId = val;
                sessionSeen = true;
                i++;
                continue;
            }
            if (token == "--json")
            {
                if (jsonSeen)
                    return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token == "--classic")
            {
                if (classicSeen)
                    return Fail("duplicate_switch", "Duplicate --classic.");
                classic = true;
                classicSeen = true;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                return Fail("unknown_switch", $"Unknown switch '{token}' for chat.");
            }
            return Fail("too_many_arguments", $"Unexpected argument '{token}' for chat.");
        }

        return Success(new ChatCommand(workspace ?? Environment.CurrentDirectory, sessionId, preset, json, classic));
    }

    private static AgentCliParseResult ParseSession(string[] args, int startIndex)
    {
        if (args.Length <= startIndex)
            return Fail("missing_subcommand", "Missing session subcommand. Expected list|fork|rewind.");

        var sub = args[startIndex];
        return sub switch
        {
            "list" => ParseSessionList(args, startIndex + 1),
            "fork" => ParseSessionFork(args, startIndex + 1),
            "rewind" => ParseSessionRewind(args, startIndex + 1),
            _ => Fail("unknown_subcommand", $"Unknown session subcommand '{sub}'. Expected list|fork|rewind.")
        };
    }

    private static AgentCliParseResult ParseSessionList(string[] args, int startIndex)
    {
        bool json = false;
        bool jsonSeen = false;
        for (int i = startIndex; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen) return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            return Fail("unknown_switch", $"Unknown switch '{token}' for session list.");
        }
        return Success(new SessionListCommand(json));
    }

    private static AgentCliParseResult ParseSessionFork(string[] args, int startIndex)
    {
        string? sessionId = null;
        int upToIndex = -1;
        string? newTitle = null;
        bool json = false;
        bool jsonSeen = false;
        bool indexSeen = false;
        bool titleSeen = false;

        for (int i = startIndex; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen) return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token == "--index")
            {
                if (indexSeen) return Fail("duplicate_switch", "Duplicate --index.");
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out upToIndex))
                    return Fail("missing_value", "Missing integer value for --index.");
                indexSeen = true;
                i++;
                continue;
            }
            if (token == "--title")
            {
                if (titleSeen) return Fail("duplicate_switch", "Duplicate --title.");
                if (i + 1 >= args.Length) return Fail("missing_value", "Missing value for --title.");
                newTitle = args[i + 1];
                titleSeen = true;
                i++;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for session fork.");

            if (sessionId is null)
            {
                if (!SessionIdLooseRegex.IsMatch(token))
                    return Fail("invalid_session_id", "Session id contains invalid characters.");
                sessionId = token;
                continue;
            }
            return Fail("too_many_arguments", $"Unexpected argument '{token}' for session fork.");
        }

        if (sessionId is null)
            return Fail("missing_session_id", "Missing required <session-id> for session fork.");

        return Success(new SessionForkCommand(sessionId, upToIndex, newTitle, json));
    }

    private static AgentCliParseResult ParseSessionRewind(string[] args, int startIndex)
    {
        string? sessionId = null;
        int toIndex = -1;
        bool json = false;
        bool jsonSeen = false;
        bool indexSeen = false;

        for (int i = startIndex; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--json")
            {
                if (jsonSeen) return Fail("duplicate_switch", "Duplicate --json.");
                json = true;
                jsonSeen = true;
                continue;
            }
            if (token == "--index")
            {
                if (indexSeen) return Fail("duplicate_switch", "Duplicate --index.");
                if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out toIndex))
                    return Fail("missing_value", "Missing integer value for --index.");
                indexSeen = true;
                i++;
                continue;
            }
            if (token.StartsWith("-", StringComparison.Ordinal))
                return Fail("unknown_switch", $"Unknown switch '{token}' for session rewind.");

            if (sessionId is null)
            {
                if (!SessionIdLooseRegex.IsMatch(token))
                    return Fail("invalid_session_id", "Session id contains invalid characters.");
                sessionId = token;
                continue;
            }
            return Fail("too_many_arguments", $"Unexpected argument '{token}' for session rewind.");
        }

        if (sessionId is null)
            return Fail("missing_session_id", "Missing required <session-id> for session rewind.");
        if (!indexSeen)
            return Fail("missing_index", "Missing required --index for session rewind.");

        return Success(new SessionRewindCommand(sessionId, toIndex, json));
    }

    private static AgentCliParseResult Fail(string code, string message) =>
        new(false, null, code, message);

    private static AgentCliParseResult Success(AgentCommand cmd) =>
        new(true, cmd, null, null);
}
