using RuAgent.Harness.Core;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace RuBackend.Harness;

public sealed record WorkspaceExternalChangeEvent(
    string Kind,
    string NormalizedPath,
    string? ExpectedSha256,
    string ObservedSha256,
    string WriterKind);

public sealed class WorkspaceExternalChangeMonitor
{
    private readonly ConcurrentDictionary<string, string> _expectedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _expectedContents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastObservedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterExpectedHash(string path, string sha256)
    {
        var normalized = NormalizeRelativePath(path);
        _ = WorkspaceExternalChangeDetector.Detect(normalized, sha256, sha256);
        _expectedHashes[normalized] = sha256;
    }

    public void RegisterExpectedHash(string workspaceRoot, string path, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var normalized = NormalizeRelativePath(path);
        _ = WorkspaceExternalChangeDetector.Detect(normalized, sha256, sha256);
        _expectedHashes[ExpectedKey(workspaceRoot, normalized)] = sha256;
    }

    public bool TryGetExpectedContent(string workspaceRoot, string path, out byte[]? content)
    {
        var key = ExpectedKey(workspaceRoot, NormalizeRelativePath(path));
        if (_expectedContents.TryGetValue(key, out var value))
        {
            content = value.ToArray();
            return true;
        }

        content = null;
        return false;
    }

    public int CaptureExpectedHashes(string workspaceRoot, int maxFiles = 4096)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (maxFiles is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFiles));
        }

        var captured = 0;
        try
        {
            foreach (var path in Directory.EnumerateFiles(workspaceRoot, "*", SearchOption.AllDirectories))
            {
                if (captured >= maxFiles)
                {
                    break;
                }

                try
                {
                    var relative = Path.GetRelativePath(workspaceRoot, path);
                    var normalized = NormalizeRelativePath(relative);
                    var bytes = File.ReadAllBytes(path);
                    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    _expectedHashes[ExpectedKey(workspaceRoot, normalized)] = hash;
                    if (bytes.Length <= 16 * 1024 * 1024)
                    {
                        _expectedContents[ExpectedKey(workspaceRoot, normalized)] = bytes;
                    }
                    captured++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return captured;
    }

    public WorkspaceExternalChangeEvent? Observe(
        string path,
        string? expectedSha256,
        string observedSha256)
    {
        var change = WorkspaceExternalChangeDetector.Detect(path, expectedSha256, observedSha256);
        return change.IsChanged
            ? new WorkspaceExternalChangeEvent(
                "workspace_external_change_detected",
                change.NormalizedPath,
                change.ExpectedSha256,
                change.ObservedSha256,
                change.WriterKind)
            : null;
    }

    public IDisposable StartWatching(
        string workspaceRoot,
        Action<WorkspaceExternalChangeEvent> onChange,
        TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        return StartWatchingCore(
            workspaceRoot,
            change =>
            {
                onChange(change);
                return Task.CompletedTask;
            },
            debounce);
    }

    public IDisposable StartWatchingAsync(
        string workspaceRoot,
        Func<WorkspaceExternalChangeEvent, Task> onChange,
        TimeSpan? debounce = null)
    {
        ArgumentNullException.ThrowIfNull(onChange);
        return StartWatchingCore(workspaceRoot, onChange, debounce);
    }

    private IDisposable StartWatchingCore(
        string workspaceRoot,
        Func<WorkspaceExternalChangeEvent, Task> onChange,
        TimeSpan? debounce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);
        Directory.CreateDirectory(root);
        var delay = debounce ?? TimeSpan.FromMilliseconds(125);
        if (delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentOutOfRangeException(nameof(debounce));
        }

        var watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler handler = (_, args) => QueueObservation(root, args.FullPath, onChange, delay);
        RenamedEventHandler renamed = (_, args) => QueueObservation(root, args.FullPath, onChange, delay);
        watcher.Changed += handler;
        watcher.Created += handler;
        watcher.Deleted += handler;
        watcher.Renamed += renamed;
        return new WatchRegistration(watcher, handler, renamed, _debounceTimers);
    }

    private void QueueObservation(
        string root,
        string fullPath,
        Func<WorkspaceExternalChangeEvent, Task> onChange,
        TimeSpan delay)
    {
        var normalized = NormalizeRelativePath(Path.GetRelativePath(root, fullPath));
        if (!_expectedHashes.TryGetValue(ExpectedKey(root, normalized), out var expected)
            && !_expectedHashes.TryGetValue(normalized, out expected))
        {
            return;
        }

        var timer = new Timer(
            _ =>
            {
                try
                {
                    var observed = File.Exists(fullPath)
                        ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fullPath))).ToLowerInvariant()
                        : new string('0', 64);
                    var change = Observe(normalized, expected, observed);
                    if (change is not null)
                    {
                        var key = ExpectedKey(root, normalized);
                        if (_lastObservedHashes.TryGetValue(key, out var previous)
                            && string.Equals(previous, observed, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }

                        _lastObservedHashes[key] = observed;

                        _ = DispatchAsync(onChange, change);
                    }
                }
                catch (IOException)
                {
                    // The authoritative pre/post hash pass retries on the next event.
                }
            },
            null,
            delay,
            Timeout.InfiniteTimeSpan);
        _debounceTimers.AddOrUpdate(normalized, timer, (_, previous) =>
        {
            previous.Dispose();
            return timer;
        });
    }

    private static async Task DispatchAsync(
        Func<WorkspaceExternalChangeEvent, Task> onChange,
        WorkspaceExternalChangeEvent change)
    {
        try
        {
            await onChange(change).ConfigureAwait(false);
        }
        catch
        {
            // The runtime owns persistence/logging failures. The watcher must remain
            // alive so a transient journal failure does not disable detection.
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path == ".." || path.StartsWith("..\\", StringComparison.Ordinal) || path.StartsWith("../", StringComparison.Ordinal))
        {
            throw new ArgumentException("Path must be workspace-relative.", nameof(path));
        }

        return path.Replace('\\', '/');
    }

    private static string ExpectedKey(string workspaceRoot, string normalizedPath) =>
        $"{Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}|{normalizedPath}";

    private sealed class WatchRegistration : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly FileSystemEventHandler _handler;
        private readonly RenamedEventHandler _renamed;
        private readonly ConcurrentDictionary<string, Timer> _timers;

        public WatchRegistration(
            FileSystemWatcher watcher,
            FileSystemEventHandler handler,
            RenamedEventHandler renamed,
            ConcurrentDictionary<string, Timer> timers)
        {
            _watcher = watcher;
            _handler = handler;
            _renamed = renamed;
            _timers = timers;
        }

        public void Dispose()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= _handler;
            _watcher.Created -= _handler;
            _watcher.Deleted -= _handler;
            _watcher.Renamed -= _renamed;
            _watcher.Dispose();
            foreach (var pair in _timers)
            {
                pair.Value.Dispose();
                _timers.TryRemove(pair.Key, out _);
            }
        }
    }
}
