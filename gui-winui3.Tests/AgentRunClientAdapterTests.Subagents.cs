using RuApp.Services;
using Xunit;
namespace RuApp.Tests;
public sealed partial class AgentRunClientAdapterTests
{
    [Fact]
    public void Subagent_client_only_exposes_observation()
    {
        Assert.Equal(new[] { "GetSubagentAsync", "ListSubagentsAsync" },
            typeof(ISubagentClient).GetMethods().Select(m => m.Name).OrderBy(x => x).ToArray());
    }
}
