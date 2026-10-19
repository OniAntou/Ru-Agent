using RuAgent.Broker.Subagents.Integration;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Tools.Git;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentWorkspaceTests
{
    [Theory]
    [InlineData("unchanged_file")]
    [InlineData("git_directory")]
    [InlineData("common_directory")]
    [InlineData("git_root")]
    public async Task Collection_revalidates_all_observed_files_and_repository_identity_after_capture(string race)
    {
        var snapshot = await new SubagentSnapshotCapture().CaptureAsync(Lease(), State, default);
        var workspace = await new SubagentWorktreeProvisioner().ProvisionAsync(snapshot, "agent_test", default);
        var cli = new CollectionRaceGit(_git, race);
        var collector = new SubagentChangeSetCollector(new SubagentGitAdapter(cli));
        var error = await Assert.ThrowsAsync<SubagentWorkspaceException>(() => collector.CollectAsync(workspace, 1, default));
        Assert.True(cli.RaceTriggered);
        Assert.Equal(race == "unchanged_file" ? "subagent_snapshot_changed" : "subagent_workspace_identity_changed", error.Code);
        Assert.Equal("clean\r\n", await File.ReadAllTextAsync(Path.Combine(Root, "edit.txt")));
    }

    private sealed class CollectionRaceGit(IGitCli inner, string race) : IGitCli
    {
        private int _inventories;
        public bool RaceTriggered { get; private set; }

        public async Task<GitCliResult> RunAsync(IReadOnlyList<string> arguments, string workingDirectory,
            CancellationToken cancellationToken, IReadOnlyDictionary<string, string>? additionalEnvironment = null)
        {
            var result = await inner.RunAsync(arguments, workingDirectory, cancellationToken, additionalEnvironment);
            if (arguments.Contains("--others") && ++_inventories == 2)
            {
                RaceTriggered = true;
                if (race == "unchanged_file")
                    await File.WriteAllTextAsync(Path.Combine(workingDirectory, "edit.txt"), "external edit after capture", cancellationToken);
            }
            var identityCommand = race switch
            {
                "git_directory" => "--absolute-git-dir",
                "common_directory" => "--git-common-dir",
                "git_root" => "--show-toplevel",
                _ => null,
            };
            return RaceTriggered && identityCommand is not null && arguments.Contains(identityCommand)
                ? result with { Stdout = Path.Combine(workingDirectory, "foreign_identity") + "\n" } : result;
        }
    }
}
