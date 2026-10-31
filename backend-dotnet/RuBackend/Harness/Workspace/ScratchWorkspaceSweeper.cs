using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RuAgent.Harness.Core;
using RuAgent.Harness.Persistence;
using RuBackend.Business.Sessions;

namespace RuBackend.Harness.Workspace;

/// <summary>
/// Reclaims scratch workspaces whose session no longer exists. The per-session delete removes its
/// own folder; this sweep is the crash, bulk-delete and manual-cleanup backstop and runs once in
/// the background after the Host starts. It never deletes a folder without a matching marker, so a
/// user folder is safe even if its name looks like a session id.
/// </summary>
public sealed class ScratchWorkspaceSweeper : IHostedService, IDisposable
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(3);

    private readonly ScratchWorkspaceStore _scratch;
    private readonly ISessionStore _sessions;
    private readonly ILogger<ScratchWorkspaceSweeper>? _logger;
    private CancellationTokenSource? _cancellation;
    private Task? _sweep;

    public ScratchWorkspaceSweeper(
        ScratchWorkspaceStore scratch,
        ISessionStore sessions,
        ILogger<ScratchWorkspaceSweeper>? logger = null)
    {
        _scratch = scratch ?? throw new ArgumentNullException(nameof(scratch));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cancellation = new CancellationTokenSource();
        _sweep = Task.Run(() => SweepAsync(_cancellation.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cancellation?.Cancel();
        if (_sweep is null)
        {
            return;
        }

        try
        {
            await _sweep.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException)
        {
            // Host shutdown does not wait on a best-effort cleanup beyond its bounded grace.
        }
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(StartupDelay, cancellationToken).ConfigureAwait(false);
            var deleted = await _scratch.DeleteOrphansAsync(
                async (sessionId, token) =>
                    await _sessions.GetAsync(sessionId, token).ConfigureAwait(false) is not null,
                cancellationToken).ConfigureAwait(false);
            if (deleted > 0)
            {
                _logger?.LogInformation("reclaimed {Count} orphaned scratch workspaces", deleted);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _logger?.LogWarning(error, "scratch workspace sweep failed");
        }
    }

    public void Dispose()
    {
        _cancellation?.Dispose();
    }
}
