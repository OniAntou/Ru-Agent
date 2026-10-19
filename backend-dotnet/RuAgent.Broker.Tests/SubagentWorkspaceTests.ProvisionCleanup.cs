using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Tools.Git;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentWorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_provision_reclaims_registered_worktree_and_snapshot_without_parent_changes(bool changedBase)
    {
        var snapshot = await new SubagentSnapshotCapture().CaptureAsync(Lease(), State, default);
        var before = (await Git("status", "--porcelain=v1")).Stdout;
        if (changedBase) File.WriteAllText(Path.Combine(snapshot.BaseDirectory, "edit.txt"), "tampered captured base");
        var git = changedBase ? _git : new FailedIndexGit(_git);
        var error = await Assert.ThrowsAsync<SubagentWorkspaceException>(() =>
            new SubagentWorktreeProvisioner(new SubagentGitAdapter(git)).ProvisionAsync(snapshot, "agent_failed", default));
        Assert.Equal(changedBase ? "subagent_snapshot_changed" : "subagent_git_failed", error.Code);
        Assert.False(Directory.Exists(Path.GetDirectoryName(snapshot.BaseDirectory)));
        Assert.DoesNotContain("agent_failed", (await Git("worktree", "list", "--porcelain")).Stdout);
        Assert.Equal(before, (await Git("status", "--porcelain=v1")).Stdout);
        Assert.Equal("clean\r\n", File.ReadAllText(Path.Combine(Root, "edit.txt")));
    }

    private sealed class FailedIndexGit(IGitCli inner) : IGitCli
    {
        public Task<GitCliResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalEnvironment = null) =>
            arguments.Contains("read-tree") ? throw new SubagentWorkspaceException("subagent_git_failed")
                : inner.RunAsync(arguments, workingDirectory, cancellationToken, additionalEnvironment);
    }
}
