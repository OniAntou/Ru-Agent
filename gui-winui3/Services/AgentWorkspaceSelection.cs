namespace RuApp.Services;

internal static class AgentWorkspaceSelection
{
    // Backend discovery needs the application root; new user work does not.
    // Explicit workspaces and restored session paths never pass through this rule.
    public static string DefaultForApplication(string applicationRoot, bool isHarness = false)
    {
        string root = Path.GetFullPath(applicationRoot);
        return isHarness ? root : Directory.GetParent(Path.TrimEndingDirectorySeparator(root))?.FullName ?? root;
    }

    public static string? Resolve(string? requestedWorkspace, string? defaultWorkspace)
    {
        if (!string.IsNullOrWhiteSpace(requestedWorkspace))
        {
            return requestedWorkspace.Trim();
        }

        return string.IsNullOrWhiteSpace(defaultWorkspace)
            ? null
            : defaultWorkspace.Trim();
    }
}
