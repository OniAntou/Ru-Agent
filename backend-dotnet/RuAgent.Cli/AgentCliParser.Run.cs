namespace RuAgent.Cli;

public static partial class AgentCliParser
{
    /// <summary>
    /// The option set shared by 'agent run' and 'agent chat'. Both commands accept the same
    /// workspace/preset/session/trust/full-access/image switches, so they parse them with one
    /// loop: the previous copy-paste pair had already drifted (only chat accepted --classic)
    /// and any new switch had to be added twice or the two commands silently disagreed.
    /// </summary>
    private sealed record RunChatOptions(
        string? Workspace,
        string Preset,
        string? SessionId,
        bool Json,
        bool TrustWorkspace,
        bool ConfirmFullAccess,
        bool Classic,
        IReadOnlyList<string> Images,
        string? Prompt,
        bool Worktree);

    private static AgentCliParseResult? ParseRunChatOptions(
        string[] args,
        int startIndex,
        bool allowClassic,
        bool allowWorktree,
        string commandLabel,
        out RunChatOptions options)
    {
        options = null!;
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
        bool worktree = false;
        bool worktreeSeen = false;
        bool classic = false;
        bool classicSeen = false;
        string? prompt = null;
        var images = new List<string>();

        for (var i = startIndex; i < args.Length; i++)
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
                var val = args[++i];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --workspace.");
                if (val.Length == 0 || val.Length > MaxWorkspacePathLength)
                    return Fail("invalid_workspace", $"Workspace path length must be 1..{MaxWorkspacePathLength}.");
                if (val.Contains('\0'))
                    return Fail("invalid_workspace", "Workspace path contains null.");
                workspace = val;
                workspaceSeen = true;
                continue;
            }

            if (token == "--preset")
            {
                if (presetSeen)
                    return Fail("duplicate_switch", "Duplicate --preset.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --preset.");
                var val = args[++i];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --preset.");
                if (val.Length == 0 || val.Length > MaxPresetLength)
                    return Fail("invalid_preset", "Preset too long.");
                if (!RuAgent.Protocol.Harness.AgentPolicyProfileCatalog.TryNormalizePresetInput(val, out var normalizedPreset))
                    return Fail("invalid_preset", $"Unknown preset '{val}'. Expected 'plan', 'standard', or 'full_access'.");
                preset = normalizedPreset;
                presetSeen = true;
                continue;
            }

            if (token == "--session")
            {
                if (sessionSeen)
                    return Fail("duplicate_switch", "Duplicate --session.");
                if (i + 1 >= args.Length)
                    return Fail("missing_value", "Missing value for --session.");
                var val = args[++i];
                if (val.StartsWith("-", StringComparison.Ordinal))
                    return Fail("missing_value", "Missing value for --session.");
                if (val.Length == 0 || val.Length > MaxSessionIdLength)
                    return Fail("invalid_session_id", $"Session id length must be 1..{MaxSessionIdLength}.");
                if (!SessionIdLooseRegex.IsMatch(val))
                    return Fail("invalid_session_id", "Session id contains invalid characters.");
                sessionId = val;
                sessionSeen = true;
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

            if (token == "--worktree" && allowWorktree)
            {
                if (worktreeSeen)
                    return Fail("duplicate_switch", "Duplicate --worktree.");
                worktree = true;
                worktreeSeen = true;
                continue;
            }

            if (token == "--classic")
            {
                if (!allowClassic)
                {
                    return Fail("unknown_switch", $"Unknown switch '{token}' for '{commandLabel}'.");
                }

                if (classicSeen)
                    return Fail("duplicate_switch", "Duplicate --classic.");
                classic = true;
                classicSeen = true;
                continue;
            }

            if (token.StartsWith("-", StringComparison.Ordinal))
            {
                return Fail("unknown_switch", $"Unknown switch '{token}' for '{commandLabel}'.");
            }

            if (!allowClassic)
            {
                // 'agent run' takes exactly one positional prompt.
                if (prompt is not null)
                    return Fail("too_many_arguments", "Multiple prompt arguments. Expected single prompt.");
                if (token.Length == 0)
                    return Fail("missing_prompt", "Prompt must be non-empty.");
                if (token.Length > MaxPromptLength)
                    return Fail("prompt_too_long", $"Prompt length {token.Length} exceeds maximum {MaxPromptLength}.");
                prompt = token;
                continue;
            }

            return Fail("too_many_arguments", $"Unexpected argument '{token}' for chat.");
        }

        if (preset == "full_access" && !confirmFullAccess)
            return Fail("full_access_confirmation_required", "--confirm-full-access is required with --preset full_access.");

        // A resumed session keeps its original workspace; a fresh worktree would
        // silently change the authority the session was created with.
        if (worktree && sessionId is not null)
            return Fail("worktree_session_conflict", "--worktree cannot be combined with --session.");

        // --json is the non-interactive rendering. It is only accepted alongside the
        // explicit trust/full-access flags, so a scripted run cannot silently take the
        // authority those flags exist to confirm.
        if (json && !trustWorkspace && !confirmFullAccess)
            return Fail("json_requires_explicit_flags", "--json requires --trust-workspace or --confirm-full-access.");

        options = new RunChatOptions(
            workspace,
            preset,
            sessionId,
            json,
            trustWorkspace,
            confirmFullAccess,
            classic,
            images.Count == 0 ? Array.Empty<string>() : images,
            prompt,
            worktree);
        return null;
    }

    private static AgentCliParseResult ParseRun(string[] args)
    {
        var error = ParseRunChatOptions(
            args, 2, allowClassic: false, allowWorktree: true, commandLabel: "agent run", out var options);
        if (error is not null)
        {
            return error;
        }

        if (options.Workspace is null)
            return Fail("missing_workspace", "Missing required --workspace for 'agent run'.");
        if (options.Prompt is null)
            return Fail("missing_prompt", "Missing required prompt argument for 'agent run'.");

        return Success(new RunCommand(
            options.Workspace,
            options.Prompt,
            options.SessionId,
            options.Preset,
            options.Json,
            options.TrustWorkspace,
            options.ConfirmFullAccess,
            options.Images.Count == 0 ? null : options.Images,
            options.Worktree));
    }

    private static AgentCliParseResult ParseChat(string[] args, int startIndex)
    {
        var error = ParseRunChatOptions(
            args, startIndex, allowClassic: true, allowWorktree: false, commandLabel: "chat", out var options);
        if (error is not null)
        {
            return error;
        }

        return Success(new ChatCommand(
            options.Workspace ?? Environment.CurrentDirectory,
            options.SessionId,
            options.Preset,
            options.Json,
            options.Classic,
            options.TrustWorkspace,
            options.ConfirmFullAccess,
            options.Images.Count == 0 ? null : options.Images,
            WorkspaceExplicit: options.Workspace is not null));
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
}
