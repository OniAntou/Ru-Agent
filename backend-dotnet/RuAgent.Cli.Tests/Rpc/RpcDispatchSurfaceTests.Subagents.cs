using RuAgent.Cli.Rpc;
using Xunit;
namespace RuAgent.Cli.Tests.Rpc;
public sealed partial class RpcDispatchSurfaceTests
{
    [Theory]
    [InlineData("subagent.send")]
    [InlineData("subagent.cancel")]
    [InlineData("subagent.apply")]
    public async Task Subagent_mutation_methods_are_not_public(string method)
    {
        var seen = new List<string>();
        var dispatcher = await CreateInitializedAsync(_ => Task.FromResult(RpcTestHost.Json("{}")), seen);
        await Assert.ThrowsAsync<RpcDispatchException>(() => dispatcher.DispatchAsync(Req(method,
            "{\"runId\":\"run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"agentId\":\"agent_one\"}"), default));
        Assert.Empty(seen);
    }
}
