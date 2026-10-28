using RuAgent.Harness.Core;

namespace RuBackend.Harness;

/// <summary>
/// Releases process-local state and execution resources after the driver has
/// cancelled pending user-apply work and owned child runs.
/// </summary>
internal sealed class AgentRuntimeRunLifecycle(
    IAgentRuntimeRootRegistry roots,
    Broker.IToolGatewayFactory brokerGateways,
    IAgentRuntimeCoordinatorFactory coordinatorFactory,
    AgentRuntimeParkingState parkingState,
    Subagents.SubagentLivePublisher? subagentLive)
{
    internal async Task ReleaseRegistrationAsync(RunId runId)
    {
        subagentLive?.Forget(runId);
        parkingState.ForgetRun(runId.Value);
        roots.Remove(runId);
        await brokerGateways.CloseRunAsync(runId.Value).ConfigureAwait(false);
        coordinatorFactory.DropRetainedApproval(runId);
    }
}
