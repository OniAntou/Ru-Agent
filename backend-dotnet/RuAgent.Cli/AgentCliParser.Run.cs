namespace RuAgent.Cli;

public static partial class AgentCliParser
{
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
        bool trustWorkspace = false;
        bool trustWorkspaceSeen = false;
        bool confirmFullAccess = false;
        bool confirmFullAccessSeen = false;
        string? prompt = null;
        var images = new List<string>();

        for (int i = 2; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--image")
            {
                var error = TryReadImageOption(args, ref i, images);
                if (error is not null)
                {
                    return error;
                }

                continue;
            }

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
                if (val != "plan" && val != "standard" && val != "full_access")
                    return Fail("invalid_preset", $"Unknown preset '{val}'. Expected 'plan', 'standard', or 'full_access'.");
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
            if (token == "--trust-workspace")
            {
                if (trustWorkspaceSeen)
                    return Fail("duplicate_switch", "Duplicate --trust-workspace.");
                trustWorkspace = true;
                trustWorkspaceSeen = true;
                continue;
            }
            if (token == "--confirm-full-access")
            {
                if (confirmFullAccessSeen)
                    return Fail("duplicate_switch", "Duplicate --confirm-full-access.");
                confirmFullAccess = true;
                confirmFullAccessSeen = true;
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

        if (preset == "full_access" && !confirmFullAccess)
            return Fail("full_access_confirmation_required", "--confirm-full-access is required with --preset full_access.");

        // --json is the non-interactive rendering. It is only accepted alongside the
        // explicit trust/full-access flags, so a scripted run cannot silently take the
        // authority those flags exist to confirm.
        if (json && !trustWorkspace && !confirmFullAccess)
            return Fail("json_requires_explicit_flags", "--json requires --trust-workspace or --confirm-full-access.");

        return Success(new RunCommand(workspace, prompt, sessionId, preset, json, trustWorkspace, confirmFullAccess, images.Count == 0 ? null : images));
    }

    /// <summary>
    /// Shared <c>--image</c> handling for run/chat: repeatable up to the attachment policy
    /// cap, value checked like the other path options.
    /// </summary>
    private static AgentCliParseResult? TryReadImageOption(string[] args, ref int i, List<string> images)
    {
        if (i + 1 >= args.Length)
        {
            return Fail("missing_value", "Missing value for --image.");
        }

        var val = args[i + 1];
        if (val.StartsWith("-", StringComparison.Ordinal))
        {
            return Fail("missing_value", "Missing value for --image.");
        }

        if (val.Length == 0 || val.Length > MaxFilePathLength || val.Contains('\0'))
        {
            return Fail("invalid_image_path", $"Image path length must be 1..{MaxFilePathLength}.");
        }

        if (images.Count >= Images.CliImagePolicy.MaxImageCount)
        {
            return Fail("too_many_images", Images.CliImagePolicy.TooManyImagesStatus);
        }

        images.Add(val);
        i++;
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
        bool trustWorkspace = false;
        bool trustWorkspaceSeen = false;
        bool confirmFullAccess = false;
        bool confirmFullAccessSeen = false;
        bool classic = false;
        bool classicSeen = false;
        var images = new List<string>();

        for (int i = startIndex; i < args.Length; i++)
        {
            var token = args[i];
            if (token == "--image")
            {
                var error = TryReadImageOption(args, ref i, images);
                if (error is not null)
                {
                    return error;
                }

                continue;
            }

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
                if (val != "plan" && val != "standard" && val != "full_access")
                    return Fail("invalid_preset", $"Unknown preset '{val}'. Expected 'plan', 'standard', or 'full_access'.");
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
            if (token == "--trust-workspace")
            {
                if (trustWorkspaceSeen)
                    return Fail("duplicate_switch", "Duplicate --trust-workspace.");
                trustWorkspace = true;
                trustWorkspaceSeen = true;
                continue;
            }
            if (token == "--confirm-full-access")
            {
                if (confirmFullAccessSeen)
                    return Fail("duplicate_switch", "Duplicate --confirm-full-access.");
                confirmFullAccess = true;
                confirmFullAccessSeen = true;
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

        if (preset == "full_access" && !confirmFullAccess)
            return Fail("full_access_confirmation_required", "--confirm-full-access is required with --preset full_access.");

        // Same contract as `agent run`: --json is only accepted alongside the explicit
        // trust/full-access flags.
        if (json && !trustWorkspace && !confirmFullAccess)
            return Fail("json_requires_explicit_flags", "--json requires --trust-workspace or --confirm-full-access.");

        return Success(new ChatCommand(workspace ?? Environment.CurrentDirectory, sessionId, preset, json, classic, trustWorkspace, confirmFullAccess, images.Count == 0 ? null : images));
    }
}
