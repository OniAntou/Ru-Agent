using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Computer;
using RuAgent.Protocol;
using RuAgent.Protocol.Harness;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private ComputerUseService? _computer;

    /// <summary>Connects the Host's computer-use service; without it every <c>computer</c> call fails by name.</summary>
    public void UseComputer(ComputerUseService computer) => _computer = computer ?? throw new ArgumentNullException(nameof(computer));

    private ValueTask<ToolExecutionOutcome> ExecuteComputerAsync(
        ToolExecutionContext context,
        JsonObject arguments,
        CancellationToken cancellationToken)
    {
        if (_computer is not { } computer)
            return ValueTask.FromResult(ToolExecutionOutcome.Failed("computer_unavailable", "Computer use is not available in this host."));
        if (!context.IsAuthenticatedV2Execution || string.IsNullOrEmpty(context.RunId)
            || !_session.TryGetRunLease(context.RunId, out var lease) || lease is null)
            return ValueTask.FromResult(ToolExecutionOutcome.Failed("computer_run_required", "Computer use needs an authorized run."));
        // A run that ended no longer holds the desktop, even before its idle timeout.
        if (computer.OwnerRunId is { } owner && owner != context.RunId
            && (!_session.TryGetRunLease(owner, out var ownerLease) || ownerLease is null || ownerLease.Lifetime.IsRevoked))
            computer.ReleaseRun(owner);
        // Grants and masking depend on the run's preset; a lease without one fails closed as standard.
        var fullAccess = string.Equals(lease.Snapshot.Preset, BrokerPolicyContracts.FullAccessPresetWireValue, StringComparison.Ordinal);
        return computer.ExecuteAsync(context.RunId, context.SessionId, fullAccess, context.ImageInputAccepted, arguments, cancellationToken);
    }
}
