using System.Text.RegularExpressions;

namespace RuAgent.Cli;

public enum AgentCommandKind
{
    Run,
    Attach,
    Trust,
    Cancel,
    Continue,
    Status,
}

public abstract record AgentCommand(AgentCommandKind Kind, bool Json);

public sealed record RunCommand(
    string WorkspacePath,
    string Prompt,
    string? SessionId,
    string Preset,
    bool Json) : AgentCommand(AgentCommandKind.Run, Json);

public sealed record AttachCommand(
    string RunId,
    bool Json) : AgentCommand(AgentCommandKind.Attach, Json);

public sealed record TrustCommand(
    string RunId,
    bool Approve) : AgentCommand(AgentCommandKind.Trust, false);

public sealed record CancelCommand(
    string RunId) : AgentCommand(AgentCommandKind.Cancel, false);

public sealed record ContinueCommand(
    string RunId) : AgentCommand(AgentCommandKind.Continue, false);

public sealed record StatusCommand(
    string RunId,
    bool Json) : AgentCommand(AgentCommandKind.Status, Json);

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

    private static readonly Regex RunIdRegex = new(@"^run_[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex SessionIdLooseRegex = new(@"^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex SessionIdStrictHexRegex = new(@"^[0-9a-f]{32}$", RegexOptions.Compiled);

    public static AgentCliParseResult Parse(string[] args)
    {
        if (args is null)
            return Fail("invalid_arguments", "Arguments null.");

        if (args.Length > MaxArgCount)
            return Fail("too_many_arguments", $"Argument count {args.Length} exceeds maximum {MaxArgCount}.");

        // Also bound each argument length to avoid huge allocations
        foreach (var a in args)
        {
            if (a is not null && a.Length > MaxPromptLength)
            {
                // Workspace/prompt specific bounds will be checked later, but cap overall arg length
                // Keep as invalid_argument if any arg absurdly long
                // For prompt, we already have MaxPromptLength, for others smaller, but this catches generic oversize
                if (a.Length > 8192)
                    return Fail("argument_too_long", $"Argument too long ({a.Length}).");
            }
        }

        if (args.Length == 0)
            return Fail("missing_command", "Expected 'agent' subcommand.");

        if (!string.Equals(args[0], "agent", StringComparison.Ordinal))
            return Fail("unknown_command", $"Unknown command '{args[0]}'. Expected 'agent'.");

        if (args.Length < 2)
            return Fail("missing_subcommand", "Missing agent subcommand. Expected run|attach|trust|cancel|continue|status.");

        var sub = args[1];
        return sub switch
        {
            "run" => ParseRun(args),
            "attach" => ParseAttach(args),
            "trust" => ParseTrust(args),
            "cancel" => ParseCancel(args),
            "continue" => ParseContinue(args),
            "status" => ParseStatus(args),
            _ => Fail("unknown_subcommand", $"Unknown agent subcommand '{sub}'.")
        };
    }

    private static AgentCliParseResult ParseRun(string[] args)
    {
        // args[0]=agent, [1]=run, [2..] options + prompt
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
                    return Fail("phase_not_available", "Preset 'full' is not available in this phase (only 'standard' allowed).");
                if (val != "standard")
                    return Fail("invalid_preset", $"Unknown preset '{val}'. Expected 'standard'.");
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
                // Additional strict check if 32 hex? allow loose for flexibility but bound already
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
            // positional prompt
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

        return Success(new RunCommand(workspace, prompt, sessionId, preset, json));
    }

    private static AgentCliParseResult ParseAttach(string[] args)
    {
        // agent attach <run-id> [--json]
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
        // agent trust <run-id> --approve|--deny
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
        // exactly one of approve/deny
        bool approve = approveSeen;
        return Success(new TrustCommand(runId, approve));
    }

    private static AgentCliParseResult ParseCancel(string[] args)
    {
        // agent cancel <run-id>
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
        // agent continue <run-id>
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
        // agent status <run-id> [--json]
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

    private static AgentCliParseResult Fail(string code, string message) =>
        new(false, null, code, message);

    private static AgentCliParseResult Success(AgentCommand cmd) =>
        new(true, cmd, null, null);
}
