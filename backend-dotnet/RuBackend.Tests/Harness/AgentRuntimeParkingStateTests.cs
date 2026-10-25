using RuAgent.Harness.Core;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class AgentRuntimeParkingStateTests
{
    [Fact]
    public void Recovered_approval_id_matches_only_the_same_approval()
    {
        var state = new AgentRuntimeParkingState();
        const string runId = "run_11111111111111111111111111111111";

        state.RecordRecoveredApproval(runId, "approval_a");

        Assert.True(state.IsRecoveredApproval(runId, "approval_a"));
        Assert.False(state.IsRecoveredApproval(runId, "approval_b"));
    }

    [Fact]
    public void Parked_approval_skips_only_the_version_that_was_recorded()
    {
        var state = new AgentRuntimeParkingState();
        const string runId = "run_22222222222222222222222222222222";

        state.RecordParkedApprovalVersion(runId, 7);
        Assert.False(state.IsParkedApprovalVersion(runId, 7));

        state.RecordRecoveredApproval(runId, "approval_a");
        state.RecordParkedApprovalVersion(runId, 7);

        Assert.True(state.IsParkedApprovalVersion(runId, 7));
        Assert.False(state.IsParkedApprovalVersion(runId, 8));
    }

    [Fact]
    public void Runnable_transition_and_run_removal_clear_their_owned_state()
    {
        var state = new AgentRuntimeParkingState();
        const string runId = "run_33333333333333333333333333333333";
        state.RecordRecoveredApproval(runId, "approval_a");
        state.RecordParkedApprovalVersion(runId, 12);
        state.MarkWaitingOnClient(runId);

        state.MarkRunnable(runId);

        Assert.False(state.IsWaitingOnClient(runId));
        Assert.False(state.IsParkedApprovalVersion(runId, 12));
        Assert.True(state.IsRecoveredApproval(runId, "approval_a"));

        state.ObserveClientWaitState(runId, HarnessRunState.WaitingWorkspaceTrust);
        Assert.True(state.IsWaitingOnClient(runId));
        state.ObserveClientWaitState(runId, HarnessRunState.Running);
        Assert.False(state.IsWaitingOnClient(runId));
        state.MarkWaitingOnClient(runId);

        state.ForgetRun(runId);

        Assert.False(state.IsWaitingOnClient(runId));
        Assert.False(state.IsParkedApprovalVersion(runId, 12));
        Assert.False(state.IsRecoveredApproval(runId, "approval_a"));
    }
}
