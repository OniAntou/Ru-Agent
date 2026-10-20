using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RuAgent.Harness.Persistence;

namespace RuBackend.Harness;

/// <summary>
/// Keeps the workspace change journal bounded: shortly after the Host starts and then every few
/// hours it removes settled entries older than <see cref="Retention"/> and trims each workspace to
/// <see cref="MaxPayloadBytesPerWorkspace"/> of retained content. A failed pass is logged and
/// retried at the next interval; it never affects a run.
/// </summary>
public sealed class WorkspaceJournalRetentionService : BackgroundService
{
    public static readonly TimeSpan Retention = TimeSpan.FromDays(14);
    public const long MaxPayloadBytesPerWorkspace = 64L * 1024 * 1024;

    private readonly WorkspaceCoordinatorStore _store;
    private readonly TimeProvider _clock;
    private readonly ILogger<WorkspaceJournalRetentionService> _logger;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _interval;

    public WorkspaceJournalRetentionService(
        WorkspaceCoordinatorStore store,
        TimeProvider clock,
        ILogger<WorkspaceJournalRetentionService> logger,
        TimeSpan? startupDelay = null,
        TimeSpan? interval = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _startupDelay = startupDelay ?? TimeSpan.FromSeconds(30);
        _interval = interval ?? TimeSpan.FromHours(6);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(_startupDelay, _clock, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await PruneOnceAsync(stoppingToken).ConfigureAwait(false);
                await Task.Delay(_interval, _clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The Host is stopping.
        }
    }

    /// <summary>One retention pass; returns null when the store refused it.</summary>
    public async Task<WorkspaceJournalPruneResult?> PruneOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            var result = await _store.PruneChangeJournalAsync(
                _clock.GetUtcNow() - Retention,
                MaxPayloadBytesPerWorkspace,
                cancellationToken).ConfigureAwait(false);
            if (result.Total > 0)
            {
                _logger.LogInformation(
                    "Workspace change journal pruned {RemovedByAge} old and {RemovedByBudget} over-budget entries",
                    result.RemovedByAge,
                    result.RemovedByBudget);
            }

            return result;
        }
        catch (HarnessStoreException error)
        {
            _logger.LogWarning(error, "Workspace change journal retention pass failed; it will run again later");
            return null;
        }
    }
}
