using System.Collections.Concurrent;
using RuAgent.Broker.Tools.Terminal;
using RuAgent.Protocol;

namespace RuAgent.Broker.Tools;

public sealed partial class ToolExecutionService
{
    private readonly ConcurrentDictionary<Task<StreamingProcessResult>, string> _terminalPumps = new();
    private readonly object _terminalLifetimeGate = new();
    private readonly HashSet<string> _stoppingSessions = new(StringComparer.Ordinal);
    private bool _stopping;

    private void TrackTerminalPump(string sessionId, Task<StreamingProcessResult> pump)
    {
        _terminalPumps[pump] = sessionId;
        _ = pump.ContinueWith(completed =>
        {
            // Detached commands still have an owner that observes completion and faults.
            _ = completed.Exception;
            _terminalPumps.TryRemove(completed, out _);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task DrainTerminalPumpsAsync(string? sessionId = null)
    {
        var pumps = _terminalPumps.Where(pair => sessionId is null || pair.Value == sessionId)
            .Select(pair => pair.Key).ToArray();
        if (pumps.Length > 0)
        {
            try
            {
                await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (TimeoutException error)
            {
                throw new TimeoutException("Terminal processes did not stop before session cleanup.", error);
            }
        }
    }

    public async Task RemoveSessionAsync(string sessionId)
    {
        await StopSessionTerminalTasksAsync(sessionId).ConfigureAwait(false);
        RemoveSessionState(sessionId);
        lock (_terminalLifetimeGate) _stoppingSessions.Remove(sessionId);
    }

    public async Task RevokeSessionGrantsAsync(string sessionId)
    {
        // Stop active work before retiring session grants.
        await StopSessionTerminalTasksAsync(sessionId).ConfigureAwait(false);
        lock (_terminalLifetimeGate) _stoppingSessions.Remove(sessionId);
    }

    private async Task StopSessionTerminalTasksAsync(string sessionId)
    {
        lock (_terminalLifetimeGate) _stoppingSessions.Add(sessionId);
        _backgroundTasks.CancelSession(sessionId);
        await DrainTerminalPumpsAsync(sessionId).ConfigureAwait(false);
        _backgroundTasks.RemoveSession(sessionId);
    }

    public async ValueTask DisposeAsync()
    {
        lock (_terminalLifetimeGate) _stopping = true;
        _backgroundTasks.CancelAll();
        await DrainTerminalPumpsAsync().ConfigureAwait(false);
        foreach (var host in _languageServers.Values)
            await host.DisposeAsync().ConfigureAwait(false);
        _languageServers.Clear();
        Dispose();
    }
}
