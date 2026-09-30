using System.Diagnostics;
using Microsoft.Extensions.Hosting;

namespace RuAgent.Hosting;

/// <summary>
/// Watches the GUI parent process that started the managed Host (via RU_PARENT_PID).
/// If the parent dies (e.g., Task Manager kill where MainWindow.OnWindowClosed is not run),
/// the Host shuts down gracefully so the Broker Job is cleaned.
/// Only active when Host is started as --managed-runtime and env var is valid.
/// </summary>
public sealed class ParentProcessWatcher : IHostedService, IDisposable
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly int _parentPid;
    private readonly DateTime? _parentStartTimeUtc;
    private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(2);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public ParentProcessWatcher(IHostApplicationLifetime lifetime)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        var raw = Environment.GetEnvironmentVariable("RU_PARENT_PID");
        if (int.TryParse(raw, out var pid) && pid > 0 && pid != Environment.ProcessId)
        {
            _parentPid = pid;
            try
            {
                using var proc = Process.GetProcessById(pid);
                _parentStartTimeUtc = proc.StartTime.ToUniversalTime();
            }
            catch
            {
                // Parent already dead or inaccessible at startup -> treat as no watcher needed
                // But we still keep pid to poll; first poll will trigger shutdown if truly dead
                _parentStartTimeUtc = null;
            }
        }
        else
        {
            _parentPid = 0;
        }
    }

    // For testing
    internal ParentProcessWatcher(IHostApplicationLifetime lifetime, int parentPid, DateTime? parentStartTimeUtc)
    {
        _lifetime = lifetime;
        _parentPid = parentPid;
        _parentStartTimeUtc = parentStartTimeUtc;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_parentPid == 0) return Task.CompletedTask;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => LoopAsync(_cts.Token), cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null) return;
        try { _cts.Cancel(); } catch { }
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken).ConfigureAwait(false); } catch { }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
    }

    private async Task LoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (IsParentDead())
            {
                _lifetime.StopApplication();
                break;
            }
        }
    }

    private bool IsParentDead()
    {
        try
        {
            using var proc = Process.GetProcessById(_parentPid);
            if (proc.HasExited) return true;
            // Only watch GUI parent; CLI tracer should not kill the shared Host when it exits.
            // RuApp is the WinUI GUI; CLI uses RuAgent.Cli or dotnet.
            try
            {
                var name = proc.ProcessName;
                if (!name.Contains("RuApp", StringComparison.OrdinalIgnoreCase))
                {
                    // Not a GUI parent -> do not treat its exit as Host shutdown signal
                    return false;
                }
            }
            catch
            {
                return false;
            }
            if (_parentStartTimeUtc.HasValue)
            {
                try
                {
                    var start = proc.StartTime.ToUniversalTime();
                    // If PID was reused, start time will differ
                    if (start != _parentStartTimeUtc.Value) return true;
                }
                catch
                {
                    // Access denied or other - treat as alive to avoid false kill
                    return false;
                }
            }
            return false;
        }
        catch (ArgumentException)
        {
            // No process with that PID -> parent dead (only for GUI parent PID)
            // If we stored a GUI PID and it's gone, shutdown.
            // For CLI parent we already returned false above, so this path is GUI only.
            return true;
        }
        catch
        {
            // Other errors -> conservative, don't shutdown
            return false;
        }
    }
}
