using RuAgent.Harness.Core;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class SubagentRunStateProjectionTests
{
    [Fact]
    public async Task Waiting_parent_has_exact_subagent_state_in_run_and_sse_projections()
    {
        await using var fixture = await BackendHarnessTestFixture.CreateAsync();
        var seeded = await fixture.SeedRunAsync(new(HarnessRunState.WaitingSubagents));
        Assert.Equal("waiting_subagents", AgentV1Service.MapRunState(seeded.Run.Status));
        Assert.Equal("waiting_subagents", HarnessEventMapper.MapStatus(seeded.Run));
        Assert.False(seeded.Run.State.IsTerminal());
    }
}
