using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RuAgent.Broker.Tools.Computer;

namespace RuBackend.Harness;

/// <summary>
/// Turns the computer-use stop hotkey into an ordinary run cancellation (ADR 0031).
/// The Broker only cancels the action in flight; ending the run is orchestration
/// and belongs to the Host.
/// </summary>
internal sealed class ComputerUseStopService(
    ComputerUseService computer,
    AgentRunLifecycleService lifecycle,
    ILogger<ComputerUseStopService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        computer.StopRequested += OnStopRequested;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        computer.StopRequested -= OnStopRequested;
        return Task.CompletedTask;
    }

    private void OnStopRequested(string runId) => _ = CancelAsync(runId);

    private async Task CancelAsync(string runId)
    {
        try
        {
            var result = await lifecycle.CancelAsync(runId, CancellationToken.None).ConfigureAwait(false);
            logger.LogInformation("computer_use.stop_hotkey status={Status}", result.StatusCode);
        }
        catch (Exception error)
        {
            logger.LogWarning("computer_use.stop_hotkey_cancel_failed kind={Kind}", error.GetType().Name);
        }
    }
}
