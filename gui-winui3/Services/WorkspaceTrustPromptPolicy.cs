namespace RuApp.Services;

/// <summary>
/// Copy for the workspace trust gate, split by first trust versus re-trust.
/// </summary>
/// <remarks>
/// A moved, recreated, or changed root presents a different canonical identity
/// at a path the user already approved. The gate stays one explicit click
/// either way; the re-trust copy exists so the second click is not a mystery
/// and names what changed. The decision itself is always the Host's.
///
/// The prompt names the folder being trusted. Without it the user approved "this
/// workspace" without being told which one, and the default for a new chat can be a
/// whole drive. The path comes from the caller's record of the run's session (the
/// Host's session list, or the exact path this app sent when it created the run);
/// when neither is known the prompt says so rather than guessing.
/// </remarks>
public static class WorkspaceTrustPromptPolicy
{
    public sealed record Prompt(
        string Title,
        string Content,
        string PrimaryButtonText,
        string WorkspaceText,
        string? ScopeWarning);

    public const string UnknownWorkspaceText = "Workspace folder not reported";

    public static Prompt For(bool isReTrust, string? workspacePath = null)
    {
        string path = string.IsNullOrWhiteSpace(workspacePath) ? UnknownWorkspaceText : workspacePath.Trim();
        string? warning = ScopeWarningFor(workspacePath);
        return isReTrust
            ? new Prompt(
                "Trust this workspace again?",
                "This folder was trusted before, but its identity changed since — it was moved, recreated, or its root files changed. "
                    + "Approve to trust the new identity; the earlier trust does not cover it.",
                "Trust again",
                path,
                warning)
            : new Prompt(
                "Trust this workspace?",
                "This run needs explicit workspace trust before it can start. Approve only if you trust this workspace and the Coding Agent.",
                "Approve",
                path,
                warning);
    }

    /// <summary>
    /// A plain statement of how wide the folder is when it is a drive root or the
    /// user's profile folder; null for an ordinary project folder.
    /// </summary>
    public static string? ScopeWarningFor(string? workspacePath, string? userProfile = null)
    {
        if (string.IsNullOrWhiteSpace(workspacePath)) return null;
        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        string? root = Path.GetPathRoot(full);
        if (!string.IsNullOrEmpty(root)
            && string.Equals(Path.TrimEndingDirectorySeparator(root), full, StringComparison.OrdinalIgnoreCase))
        {
            return $"This is the root of drive {root.TrimEnd('\\', '/')}. Trusting it lets the agent work with files anywhere on the drive.";
        }

        string profile = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)
            && string.Equals(Path.TrimEndingDirectorySeparator(profile), full, StringComparison.OrdinalIgnoreCase))
        {
            return "This is your user folder. Trusting it lets the agent work with files across your profile.";
        }

        return null;
    }
}
