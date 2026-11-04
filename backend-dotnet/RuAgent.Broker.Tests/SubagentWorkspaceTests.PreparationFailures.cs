using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Tools.Git;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentWorkspaceTests
{
    // Read 1 decides the source (a failure there now means "use a private base", ADR 0042),
    // read 2 is the parent HEAD before allocation, read 3 the recheck after copying.
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public async Task Same_git_failure_is_known_only_before_allocation(int failedHeadRead, bool unchanged)
    {
        var capture = new SubagentSnapshotCapture(new SubagentGitAdapter(new FailingHeadGit(_git, failedHeadRead)));
        var error = await Assert.ThrowsAsync<SubagentWorkspaceException>(() => capture.CaptureAsync(Lease(), State, default));
        Assert.Equal("subagent_git_head_unavailable", error.Code);
        Assert.Equal(unchanged, error.NoWorkspaceAllocated);
        Assert.Equal("clean\r\n", File.ReadAllText(Path.Combine(Root, "edit.txt")));
        if (Directory.Exists(State)) Assert.Empty(Directory.EnumerateFileSystemEntries(State));
    }

    private sealed class FailingHeadGit(IGitCli inner, int failedRead) : IGitCli
    {
        private int _reads;
        public Task<GitCliResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalEnvironment = null) =>
            arguments.Contains("HEAD^{commit}") && ++_reads == failedRead
                ? Task.FromResult(new GitCliResult(false, 128, "", "synthetic failure", "git_failed"))
                : inner.RunAsync(arguments, workingDirectory, cancellationToken, additionalEnvironment);
    }
}
