namespace RuAgent.Harness.Core;

/// <summary>
/// Implemented by gateways that own live tool processes. Lets the coordinator forward
/// run-scoped cancellation so in-flight terminal executions terminate their whole
/// process tree and return cancelled receipts instead of being abandoned.
/// </summary>
public interface IToolRunCancellation
{
    Task CancelRunAsync(RunId runId, CancellationToken cancellationToken);
}
