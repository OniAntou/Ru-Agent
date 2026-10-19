using RuAgent.Broker.Subagents.Workspaces;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentWorkspaceTests
{
    [Fact]
    public async Task Provision_rejects_replaced_git_directory_with_same_paths_and_repository_content()
    {
        var snapshot = await new SubagentSnapshotCapture().CaptureAsync(Lease(), State, default);
        var saved = Path.Combine(_owned, "original-git");
        Directory.Move(snapshot.GitDirectory, saved);
        Directory.CreateDirectory(snapshot.GitDirectory);
        foreach (var directory in Directory.EnumerateDirectories(saved, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(snapshot.GitDirectory, Path.GetRelativePath(saved, directory)));
        foreach (var file in Directory.EnumerateFiles(saved, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(snapshot.GitDirectory, Path.GetRelativePath(saved, file)));
        var before = (await Git("status", "--porcelain=v1")).Stdout;
        var error = await Assert.ThrowsAsync<SubagentWorkspaceException>(() =>
            new SubagentWorktreeProvisioner().ProvisionAsync(snapshot, "agent_replaced", default));
        Assert.Equal("subagent_workspace_identity_changed", error.Code);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(snapshot.BaseDirectory)!, "agent_replaced")));
        Assert.False(Directory.Exists(Path.Combine(snapshot.CommonDirectory, "worktrees")));
        Assert.Equal(before, (await Git("status", "--porcelain=v1")).Stdout);
    }
}
