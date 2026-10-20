using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Persistence;
using RuBackend.Harness;
using Xunit;

using RuAgent.Protocol.Harness;
namespace RuBackend.Tests.Harness;

public sealed class ContinueGateClearTests
{
    [Fact]
    public void Continue_without_gate_clears_nothing_and_commits_one_event()
    {
        var run = WaitingUserRun();

        var (next, events) = AgentV1Service.PlanContinueCommit(
            run,
            new HarnessRunStatus(HarnessRunState.Running),
            clearGate: false,
            new CryptographicIdGenerator(),
            DateTimeOffset.UtcNow);

        var single = Assert.Single(events);
        Assert.Equal(HarnessEventKind.ContinueRequested, single.Kind);
        Assert.Equal("continued", single.SafeCode);
        Assert.Equal(HarnessRunState.Running, next.State);
        Assert.Equal(run.LastSequence + 1, next.LastSequence);
    }

    [Fact]
    public void Continue_with_gate_cleared_audits_the_override()
    {
        var run = WaitingUserRun();

        var (next, events) = AgentV1Service.PlanContinueCommit(
            run,
            new HarnessRunStatus(HarnessRunState.Running),
            clearGate: true,
            new CryptographicIdGenerator(),
            DateTimeOffset.UtcNow);

        Assert.Equal(2, events.Length);
        Assert.Equal(HarnessEventKind.ContinueRequested, events[0].Kind);
        Assert.Equal(HarnessEventKind.ReconciliationResolved, events[1].Kind);
        Assert.Equal("gate_cleared_by_user", events[1].SafeCode);
        Assert.Equal(HarnessRunState.Running, next.State);
        Assert.Equal(run.LastSequence + 2, next.LastSequence);
    }

    private static AgentRun WaitingUserRun() =>
        new(
            new RunId("run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            new SessionId("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            new WorkspaceId("ws_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"),
            new HarnessRunStatus(HarnessRunState.WaitingUser),
            3,
            7,
            2,
            0,
            0,
            AgentPermissionPreset.Standard,
            1,
            new string('a', 64),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow);
}
