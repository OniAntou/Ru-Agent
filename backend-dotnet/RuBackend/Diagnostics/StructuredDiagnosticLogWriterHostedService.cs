using Microsoft.Extensions.Hosting;

namespace RuBackend.Diagnostics;

/// <summary>
/// Gives the generic host an explicit shutdown owner for the diagnostic writer.
/// The outcome is retained for host-level observability and tests; the writer
/// remains responsible for quiescence and primitive cleanup.
/// </summary>
public sealed class StructuredDiagnosticLogWriterHostedService : IHostedService
{
    private readonly StructuredDiagnosticLogWriter _writer;
    private readonly TimeSpan _shutdownBudget;

    public StructuredDiagnosticLogWriterHostedService(
        StructuredDiagnosticLogWriter writer,
        TimeSpan? shutdownBudget = null)
    {
        _writer = writer;
        _shutdownBudget = shutdownBudget ?? TimeSpan.FromSeconds(2);
    }

    public DiagnosticShutdownOutcome? LastShutdownOutcome { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        LastShutdownOutcome = await _writer.StopAsync(_shutdownBudget, cancellationToken)
            .ConfigureAwait(false);
    }
}
