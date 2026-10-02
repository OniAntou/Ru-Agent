using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgChatLoopTests
{
    [Fact]
    public async Task ExecuteSubmittedLine_DispatchesSlashCommandWithoutRendering()
    {
        var seen = new List<string>();
        var dispatcher = new AgentCommands.ChatLineDispatcher(
            handleSlash: line => { seen.Add("slash:" + line); return Task.FromResult(true); },
            handlePrompt: (line, _) => { seen.Add("prompt:" + line); return Task.CompletedTask; });

        Assert.True(await dispatcher.DispatchAsync("/status", CancellationToken.None));
        Assert.False(await dispatcher.DispatchAsync("sửa lỗi", CancellationToken.None));
        Assert.Equal(["slash:/status", "prompt:sửa lỗi"], seen);
    }
}
