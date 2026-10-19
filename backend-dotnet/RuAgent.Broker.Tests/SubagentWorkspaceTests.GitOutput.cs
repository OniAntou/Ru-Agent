using System.Text;
using RuAgent.Broker.Subagents.Workspaces;
using RuAgent.Broker.Tools.Git;
using RuAgent.Broker.Tools.Terminal;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed partial class SubagentWorkspaceTests
{
    [Fact]
    public async Task Snapshot_index_larger_than_ordinary_git_output_limit_is_complete()
    {
        const int additionalFiles = 3000;
        for (var n = 0; n < additionalFiles; n++)
            File.WriteAllText(Path.Combine(Root, new string('a', 64) + n.ToString("D4") + ".txt"), "");
        await Git("add", ".");

        // A full head buffer distinguishes the process output limit from display truncation.
        var ordinary = new StreamingGitCli(new StreamingProcessRunner(
            outputLimitBytes: 8 * 1024 * 1024, headCapacityChars: 8 * 1024 * 1024, tailCapacityChars: 0));
        var bounded = await ordinary.RunAsync(["ls-files", "--stage", "-z"], Root, default);
        Assert.False(bounded.Succeeded);
        Assert.Equal("git_output_truncated", bounded.FailureReason);

        var index = await new SubagentGitAdapter().Index(Root, default);
        Assert.True(Encoding.UTF8.GetByteCount(index) > 256 * 1024);
        Assert.Equal(additionalFiles + 3, index.Split('\0', StringSplitOptions.RemoveEmptyEntries).Length);
    }
}
