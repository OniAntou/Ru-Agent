namespace RuApp.Services;

internal static class AgentWorkspaceSelection
{
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
