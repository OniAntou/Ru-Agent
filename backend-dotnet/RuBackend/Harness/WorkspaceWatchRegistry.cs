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
                        entry.Watcher = monitor.StartWatchingAsync(root, async change =>
                        {
                            bool persisted;
                            try { persisted = await persistChange(change).ConfigureAwait(false); }
                            catch
                            {
                                monitor.ForgetObservation(root, change.NormalizedPath);
                                throw;
                            }

                            if (!persisted)
                            {
                                monitor.ForgetObservation(root, change.NormalizedPath);
                                return;
                            }

                            lock (entry.Gate)
                            {
                                // A callback already persisting when its watch ends
                                // must not acknowledge into a replacement baseline.
                                if (!entry.Retired)
                                    monitor.RegisterExpectedHash(root, change.NormalizedPath, change.ObservedSha256);
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
