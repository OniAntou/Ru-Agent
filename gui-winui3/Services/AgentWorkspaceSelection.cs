namespace RuApp.Services;

internal static class AgentWorkspaceSelection
{
    /// <summary>
    /// The GUI has no project of its own: a chat that never chose a folder must not inherit the
    /// application directory or its parent drive. The Host allocates a Ru-owned scratch workspace
    /// for that session, and only the harness keeps its explicit fixture root.
    /// </summary>
    public static string? DefaultForApplication(string applicationRoot, bool isHarness = false)
    {
        string root = Path.GetFullPath(applicationRoot);
        return isHarness ? root : null;
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
