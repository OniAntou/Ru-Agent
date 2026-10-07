namespace RuAgent.Cli;

public static partial class AgentCliParser
{
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
        // agent approve <run-id> <approval-id> [--always] [--json]
        bool json = false;
        bool jsonSeen = false;
        bool always = false;
        bool alwaysSeen = false;
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
            if (token == "--always")
            {
                if (alwaysSeen)
                    return Fail("duplicate_switch", "Duplicate --always.");
                always = true;
                alwaysSeen = true;
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

        return Success(new ApproveCommand(runId, approvalId, ApprovalScope.Once, json, always));
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

    private static AgentCliParseResult ParseAnswer(string[] args)
    {
        // agent answer <run-id> <input-id> [answer] [--json]
        bool json = false;
        bool jsonSeen = false;
        string? runId = null;
        string? inputId = null;
        string? answer = null;

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
                return Fail("unknown_switch", $"Unknown switch '{token}' for 'agent answer'.");
            if (runId is null)
            {
                var validation = ValidateRunId(token);
                if (validation is not null) return validation;
                runId = token;
                continue;
            }
            if (inputId is null)
            {
                var validation = ValidateInputId(token);
                if (validation is not null) return validation;
                inputId = token;
                continue;
            }
            if (answer is not null)
                return Fail("too_many_arguments", "Too many positional arguments for 'agent answer'.");
            if (token.Length == 0 || token.Contains('\0'))
                return Fail("invalid_answer", "Answer must be a non-empty bounded string.");
            answer = token;
        }

        if (runId is null)
            return Fail("missing_run_id", "Missing required <run-id> for 'agent answer'.");
        if (inputId is null)
            return Fail("missing_input_id", "Missing required <input-id> for 'agent answer'.");
        if (json && answer is null)
            return Fail("interactive_required", "--json requires the answer argument; it cannot prompt.");

        return Success(new AnswerCommand(runId, inputId, answer, json));
    }

    private static AgentCliParseResult? ValidateInputId(string inputId)
    {
        if (inputId.Length != MaxInputIdLength)
            return Fail("invalid_input_id", $"Input id must be length {MaxInputIdLength} (inp_ + 32 hex). Got {inputId.Length}.");
        if (!InputIdRegex.IsMatch(inputId))
            return Fail("invalid_input_id", $"Input id must match inp_[0-9a-f]{{32}}. Got '{inputId}'.");
        return null;
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

    private static AgentCliParseResult? ValidateApprovalId(string approvalId)
    {
        // Exactly the format the host mints (apr_ + 32 lowercase hex). Accepting
        // any apr_-prefixed token let a truncated or mistyped id through to a 404
        // that reads like the approval no longer exists.
        if (approvalId.Length != MaxApprovalIdLength)
            return Fail("invalid_approval_id", $"Approval id must be length {MaxApprovalIdLength} (apr_ + 32 hex). Got {approvalId.Length}.");
        if (!ApprovalIdRegex.IsMatch(approvalId))
            return Fail("invalid_approval_id", $"Approval id must match apr_[0-9a-f]{{32}}. Got '{approvalId}'.");
        return null;
    }
}
