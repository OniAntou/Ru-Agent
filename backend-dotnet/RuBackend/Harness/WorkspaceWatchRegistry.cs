using System.Collections.Concurrent;

namespace RuBackend.Harness;

/// <summary>Owns one baseline and watcher until the last drive leaves a workspace.</summary>
internal sealed class WorkspaceWatchRegistry(WorkspaceExternalChangeMonitor monitor)
{
    private readonly ConcurrentDictionary<string, Entry> _workspaces = new(StringComparer.OrdinalIgnoreCase);

    public IDisposable Acquire(string workspaceRoot, Func<WorkspaceExternalChangeEvent, Task<bool>> persistChange)
    {
        ArgumentNullException.ThrowIfNull(persistChange);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        while (true)
        {
            var entry = _workspaces.GetOrAdd(root, _ => new Entry());
            lock (entry.Gate)
            {
                if (entry.Retired) continue;
                if (entry.References == 0)
                {
                    try
                    {
                        monitor.CaptureExpectedHashes(root);
                        entry.PersistChange = persistChange;
                        entry.Watcher = monitor.StartWatchingAsync(root, async change =>
                        {
                            try { await PersistObservedAsync(root, entry, change, CancellationToken.None).ConfigureAwait(false); }
                            catch
                            {
                                monitor.ForgetObservation(root, change.NormalizedPath);
                                throw;
                            }
                        });
                    }
                    catch
                    {
                        Retire(root, entry);
                        throw;
                    }
                }

                entry.References++;
                return new Lease(() => Release(root, entry));
            }
        }
    }

    public async Task ObserveKnownPathAsync(string workspaceRoot, string path, CancellationToken ct)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspaceRoot));
        if (!_workspaces.TryGetValue(root, out var entry)) return;
        var change = new WorkspaceExternalChangeEvent("workspace_tool_change_detected", path, null,
            new string('0', 64), "ru_tool");
        if (!await PersistObservedAsync(root, entry, change, ct).ConfigureAwait(false))
            throw new RuAgent.Harness.Persistence.HarnessStoreException("workspace_change_store_failed");
    }

    private async Task<bool> PersistObservedAsync(string root, Entry entry, WorkspaceExternalChangeEvent candidate, CancellationToken ct)
    {
        await entry.ChangeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (entry.Gate) { if (entry.Retired) return true; }
            // A queued watcher event may describe an already acknowledged tool
            // write. Re-read just this path under the shared persistence gate.
            var change = monitor.RefreshObservation(root, candidate);
            if (change is null) return true;
            if (!await entry.PersistChange!(change).ConfigureAwait(false))
            {
                monitor.ForgetObservation(root, change.NormalizedPath);
                return false;
            }
            lock (entry.Gate)
                if (!entry.Retired) monitor.RegisterExpectedHash(root, change.NormalizedPath, change.ObservedSha256);
            return true;
        }
        finally { entry.ChangeGate.Release(); }
    }

    private void Release(string root, Entry entry)
    {
        lock (entry.Gate)
        {
            if (--entry.References == 0) Retire(root, entry);
        }
    }

    private void Retire(string root, Entry entry)
    {
        entry.Retired = true;
        try { entry.Watcher?.Dispose(); }
        finally
        {
            monitor.ClearExpectedHashes(root);
            _workspaces.TryRemove(new KeyValuePair<string, Entry>(root, entry));
        }
    }

    private sealed class Entry
    {
        public object Gate { get; } = new();
        public SemaphoreSlim ChangeGate { get; } = new(1, 1);
        public Func<WorkspaceExternalChangeEvent, Task<bool>>? PersistChange;
        public int References;
        public bool Retired;
        public IDisposable? Watcher;
    }

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
