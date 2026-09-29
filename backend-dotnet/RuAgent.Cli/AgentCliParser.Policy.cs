namespace RuAgent.Cli;

public static partial class AgentCliParser
{
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

    private static AgentCliParseResult? ValidateRuleId(string ruleId)
    {
        if (ruleId.Length != MaxRuleIdLength)
            return Fail("invalid_rule_id", $"Rule id must be length {MaxRuleIdLength} (rule_ + 32 hex). Got {ruleId.Length}.");
        if (!RuleIdRegex.IsMatch(ruleId))
            return Fail("invalid_rule_id", $"Rule id must match rule_[0-9a-f]{{32}}. Got '{ruleId}'.");
        return null;
    }
}
