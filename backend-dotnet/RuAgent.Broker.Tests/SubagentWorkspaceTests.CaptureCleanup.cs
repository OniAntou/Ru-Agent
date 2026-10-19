using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Tools.Git;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentWorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_capture_reclaims_partial_copy_and_reports_cleanup_failure(bool blockedCleanup)
    {
        using var cancel = new CancellationTokenSource();
        var before = (await Git("status", "--porcelain=v1")).Stdout;
        var cli = new CaptureCancellationGit(_git, cancel, State, blockedCleanup);
        var capture = new SubagentSnapshotCapture(new SubagentGitAdapter(cli));
        if (blockedCleanup)
        {
            var error = await Assert.ThrowsAsync<SubagentWorkspaceException>(() => capture.CaptureAsync(Lease(), State, cancel.Token));
            Assert.Equal("subagent_snapshot_cleanup_incomplete", error.Code);
            Assert.Equal("retained until retry", File.ReadAllText(Path.Combine(Assert.Single(Directory.GetDirectories(State)), "readonly.txt")));
            Assert.Contains(Assert.IsType<AggregateException>(error.InnerException).InnerExceptions,
                e => e is OperationCanceledException canceled && canceled.CancellationToken == cancel.Token);
        }
        else
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture.CaptureAsync(Lease(), State, cancel.Token));
            Assert.Equal(cancel.Token, error.CancellationToken);
            Assert.Empty(Directory.EnumerateFileSystemEntries(State));
        }
        Assert.True(cancel.IsCancellationRequested);
        Assert.True(cli.SawCopiedFiles);
        Assert.Equal(before, (await Git("status", "--porcelain=v1")).Stdout);
    }

    private sealed class CaptureCancellationGit(IGitCli inner, CancellationTokenSource cancel,
        string state, bool blockedCleanup) : IGitCli
    {
        private int _heads;
        public bool SawCopiedFiles { get; private set; }

        public Task<GitCliResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalEnvironment = null)
        {
            if (arguments.Contains("HEAD^{commit}") && ++_heads == 2)
            {
                var allocated = Assert.Single(Directory.GetDirectories(state));
                SawCopiedFiles = Directory.EnumerateFiles(Path.Combine(allocated, "base")).Any();
                if (blockedCleanup)
                {
                    var file = Path.Combine(allocated, "readonly.txt");
                    File.WriteAllText(file, "retained until retry");
                    File.SetAttributes(file, FileAttributes.ReadOnly);
                }
                cancel.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return inner.RunAsync(arguments, workingDirectory, cancellationToken, additionalEnvironment);
        }
    }
}
