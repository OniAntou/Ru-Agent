namespace RuBackend.Harness.Subagents;

/// <summary>Host configuration, shared by provisioning, collection and owned cleanup.</summary>
public sealed class SubagentWorkspaceStorage(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
}
