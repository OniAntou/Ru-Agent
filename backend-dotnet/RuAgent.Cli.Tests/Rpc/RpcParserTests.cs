using RuAgent.Cli;
using Xunit;

namespace RuAgent.Cli.Tests.Rpc;

public sealed class RpcParserTests
{
    [Fact]
    public void ParseRpc_accepts_the_bare_subcommand()
    {
        var result = AgentCliParser.Parse(["agent", "rpc"]);
        Assert.True(result.Success, result.ErrorCode);
        Assert.IsType<RpcCommand>(result.Command);
    }

    [Theory]
    [InlineData("agent", "rpc", "--json")]
    [InlineData("agent", "rpc", "extra")]
    [InlineData("agent", "rpc", "--workspace", "C:\\x")]
    public void ParseRpc_rejects_unknown_arguments(params string[] args)
    {
        var result = AgentCliParser.Parse(args);
        Assert.False(result.Success);
        Assert.NotNull(result.ErrorCode);
    }
}
