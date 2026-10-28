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
    private const int MaxExpectedContentBytes = 16 * 1024 * 1024;

    /// <summary>
    /// A baseline keeps the file's previous content only up to this size: the journal stores that
    /// content so a change can be restored, and a restore of a multi-megabyte file is rare while
    /// storing it for every change filled the database. Larger files keep their hash only.
    /// </summary>
    public const int MaxRetainedContentBytes = 1024 * 1024;

    private static readonly string[] VolatileDirectories =
        [".git", "node_modules", "__pycache__", ".vs", "bin", "obj", ".pytest_cache", ".mypy_cache", ".ruff_cache"];

    private static readonly string[] VolatileSuffixes =
        ["-wal", "-shm", "-journal", ".sqlite", ".sqlite3", ".db", ".log", ".tmp", ".lock"];

    private readonly ConcurrentDictionary<string, string> _expectedHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _expectedContents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastObservedHashes = new(StringComparer.OrdinalIgnoreCase);

    public void RegisterExpectedHash(string path, string sha256)
    {
        var normalized = NormalizeRelativePath(path);
        _ = WorkspaceExternalChangeDetector.Detect(normalized, sha256, sha256);
        _expectedHashes[normalized] = sha256;
        _expectedContents.TryRemove(normalized, out _);
        _lastObservedHashes.TryRemove(normalized, out _);
    }

    public void RegisterExpectedHash(string workspaceRoot, string path, string sha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var normalized = NormalizeRelativePath(path);
        _ = WorkspaceExternalChangeDetector.Detect(normalized, sha256, sha256);
        var key = ExpectedKey(workspaceRoot, normalized);
        _expectedHashes[key] = sha256;
        _expectedContents.TryRemove(key, out _);
        _lastObservedHashes.TryRemove(key, out _);
    }

    /// <summary>
    /// Drops the "already reported" mark after a change could not be journaled,
    /// so the next watcher event or rescan reports the same content again.
    /// </summary>
    public void ForgetObservation(string workspaceRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _lastObservedHashes.TryRemove(ExpectedKey(workspaceRoot, NormalizeRelativePath(path)), out _);
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

    internal WorkspaceExternalChangeEvent? RefreshObservation(string root, WorkspaceExternalChangeEvent change)
    {
        var path = NormalizeRelativePath(change.NormalizedPath);
        var key = ExpectedKey(root, path);
        _expectedHashes.TryGetValue(key, out var expected);
        var fullPath = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        var observed = File.Exists(fullPath) ? ComputeHashAndOptionalContent(fullPath).Hash : new string('0', 64);
        return string.Equals(expected, observed, StringComparison.OrdinalIgnoreCase)
            ? null : change with { ExpectedSha256 = expected, ObservedSha256 = observed };
    }

    /// <summary>
    /// Files larger than this are never hashed into the drive-start baseline:
    /// reading multi-gigabyte binaries on every first drive would stall the run
    /// for minutes on a large workspace. The live watcher still reports changes
    /// to them while the drive runs.
    /// </summary>
    public const long MaxHashedFileBytes = MaxExpectedContentBytes;

    /// <summary>
    /// Total bytes hashed per baseline capture. Together with the file-count cap
    /// this bounds drive-start latency on huge workspaces; coverage past either
    /// cap was already best-effort, so stopping early weakens nothing new.
    /// </summary>
    public const long MaxCaptureTotalBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Whether a file churns on its own (database files and their write-ahead logs, logs, lock and
    /// temporary files) or lives in a generated or version-control folder. The bulk baseline skips
    /// them: watching them only fills the journal with changes nobody would restore, and in a
    /// workspace that contains the Ru profile it recorded the journal's own database.
    /// </summary>
    public static bool IsVolatilePath(string normalizedPath)
    {
        ArgumentNullException.ThrowIfNull(normalizedPath);
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return false;
        }

        for (var index = 0; index < segments.Length - 1; index++)
        {
            if (VolatileDirectories.Contains(segments[index], StringComparer.OrdinalIgnoreCase))
            {
                return true;
            }

            // The profile of this application when the workspace is a home folder.
            if (index + 2 < segments.Length
                && string.Equals(segments[index], "AppData", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[index + 1], "Local", StringComparison.OrdinalIgnoreCase)
                && string.Equals(segments[index + 2], "Ru", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var name = segments[^1];
        return VolatileSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public int CaptureExpectedHashes(string workspaceRoot, int maxFiles = 4096, long maxTotalBytes = MaxCaptureTotalBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        if (maxFiles is < 1 or > 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFiles));
        }

        if (maxTotalBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));
        }

        ClearExpectedHashes(workspaceRoot);
        var captured = 0;
        var totalBytes = 0L;
        try
        {
            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline,
            };
            foreach (var path in Directory.EnumerateFiles(workspaceRoot, "*", enumerationOptions))
            {
                if (captured >= maxFiles || totalBytes >= maxTotalBytes)
                {
                    break;
                }

                try
                {
                    // Size first so an oversized file costs a stat, never a
                    // multi-gigabyte read (and never hydrates a cloud placeholder).
                    var length = new FileInfo(path).Length;
                    if (length > MaxHashedFileBytes)
                    {
                        continue;
                    }

                    var relative = Path.GetRelativePath(workspaceRoot, path);
                    var normalized = NormalizeRelativePath(relative);
                    if (IsVolatilePath(normalized))
                    {
                        continue;
                    }

                    var snapshot = ComputeHashAndOptionalContent(path);
                    totalBytes += length;
                    var key = ExpectedKey(workspaceRoot, normalized);
                    _expectedHashes[key] = snapshot.Hash;
                    _lastObservedHashes.TryRemove(key, out _);
                    if (snapshot.Content is not null)
                    {
                        _expectedContents[key] = snapshot.Content;
                    }
                    else
                    {
                        _expectedContents.TryRemove(key, out _);
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

    public int ClearExpectedHashes(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var prefix = Path.GetFullPath(workspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + "|";
        var removed = 0;
        foreach (var pair in _expectedHashes.Keys.ToArray())
        {
            if (!pair.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_expectedHashes.TryRemove(pair, out _))
            {
                removed++;
            }
            _expectedContents.TryRemove(pair, out _);
            _lastObservedHashes.TryRemove(pair, out _);
        }

        return removed;
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
        };
        var registrationCts = new CancellationTokenSource();
        // Notifications already queued by the OS may arrive after disposal.
        // The token remains usable after its source is disposed.
        var registrationCancellation = registrationCts.Token;
        var debounceTimers = new ConcurrentDictionary<string, Timer>(StringComparer.OrdinalIgnoreCase);
        var rescanGate = new SemaphoreSlim(1, 1);
        FileSystemEventHandler handler = (_, args) => QueueObservation(root, args.FullPath, onChange, delay, debounceTimers, registrationCancellation);
        RenamedEventHandler renamed = (_, args) => QueueObservation(root, args.FullPath, onChange, delay, debounceTimers, registrationCancellation);
        ErrorEventHandler error = (_, _) => _ = RescanExpectedFilesAsync(
            root,
            onChange,
            rescanGate,
            registrationCancellation);
        watcher.Changed += handler;
        watcher.Created += handler;
        watcher.Deleted += handler;
        watcher.Renamed += renamed;
        watcher.Error += error;
        watcher.EnableRaisingEvents = true;
        return new WatchRegistration(watcher, handler, renamed, error, debounceTimers, registrationCts, rescanGate);
    }

    private void QueueObservation(
        string root,
        string fullPath,
        Func<WorkspaceExternalChangeEvent, Task> onChange,
        TimeSpan delay,
        ConcurrentDictionary<string, Timer> debounceTimers,
        CancellationToken registrationCancellation)
    {
        if (registrationCancellation.IsCancellationRequested)
        {
            return;
        }
        string normalized;
        try
        {
            normalized = NormalizeRelativePath(Path.GetRelativePath(root, fullPath));
        }
        catch (ArgumentException)
        {
            // FileSystemWatcher can surface paths through a junction/reparse
            // boundary that are no longer under the watched root. Ignore them
            // without allowing an event-thread exception to escape.
            return;
        }
        if (!_expectedHashes.ContainsKey(ExpectedKey(root, normalized))
            && !_expectedHashes.ContainsKey(normalized))
        {
            return;
        }

        Timer? timer = null;
        timer = new Timer(
            _ =>
            {
                try
                {
                    if (registrationCancellation.IsCancellationRequested)
                    {
                        return;
                    }

                    // A baseline may be updated while the debounce timer is
                    // pending. Read it at observation time so an already
                    // acknowledged write is not reported as an external change.
                    if (!_expectedHashes.TryGetValue(ExpectedKey(root, normalized), out var expected)
                        && !_expectedHashes.TryGetValue(normalized, out expected))
                    {
                        return;
                    }

                    var observed = File.Exists(fullPath)
                        ? ComputeHashAndOptionalContent(fullPath).Hash
                        : new string('0', 64);
                    var change = Observe(normalized, expected, observed);
                    if (change is not null)
                    {
                        var key = ExpectedKey(root, normalized);
                        if (!TryMarkObserved(key, observed))
                        {
                            return;
                        }

                        _ = DispatchAsync(onChange, change, registrationCancellation);
                    }
                }
                catch (IOException)
                {
                    // The authoritative pre/post hash pass retries on the next event.
                }
                catch (UnauthorizedAccessException)
                {
                    // The file may have become inaccessible between the watcher
                    // notification and the hash pass; retry on a later event.
                }
                catch (Exception error) when (error is NotSupportedException or ArgumentException
                    or System.Security.SecurityException)
                {
                    // A timer callback has no caller: an exception here would
                    // terminate the Host. Retry on the next event.
                }
                finally
                {
                    if (timer is not null
                        && debounceTimers.TryGetValue(normalized, out var current)
                        && ReferenceEquals(current, timer))
                    {
                        ((ICollection<KeyValuePair<string, Timer>>)debounceTimers)
                            .Remove(new KeyValuePair<string, Timer>(normalized, timer));
                    }

                    timer?.Dispose();
                }
            },
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        var scheduledTimer = timer!;
        debounceTimers.AddOrUpdate(normalized, scheduledTimer, (_, previous) =>
        {
            previous.Dispose();
            return scheduledTimer;
        });
        scheduledTimer.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private static async Task DispatchAsync(
        Func<WorkspaceExternalChangeEvent, Task> onChange,
        WorkspaceExternalChangeEvent change,
        CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            await onChange(change).ConfigureAwait(false);
        }
        catch
        {
            // The runtime owns persistence/logging failures. The watcher must remain
            // alive so a transient journal failure does not disable detection.
        }
    }

    private async Task RescanExpectedFilesAsync(
        string workspaceRoot,
        Func<WorkspaceExternalChangeEvent, Task> onChange,
        SemaphoreSlim rescanGate,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await rescanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            var root = Path.GetFullPath(workspaceRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var prefix = root + "|";
            foreach (var pair in _expectedHashes.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var normalized = pair.Key[prefix.Length..];
                string fullPath;
                try
                {
                    fullPath = Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
                    var observed = File.Exists(fullPath)
                        ? ComputeHashAndOptionalContent(fullPath).Hash
                        : new string('0', 64);
                    var change = Observe(normalized, pair.Value, observed);
                    if (change is not null && TryMarkObserved(pair.Key, observed))
                    {
                        await DispatchAsync(onChange, change, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (ArgumentException)
                {
                    // Ignore a corrupt key or path that no longer resolves under
                    // the watched root.
                }
                catch (IOException)
                {
                    // A concurrent writer may make the file unreadable; the next
                    // watcher error or normal event will retry the observation.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same retry semantics for ACL transitions.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            if (acquired)
            {
                try { rescanGate.Release(); } catch (ObjectDisposedException) { }
            }
        }
    }

    private static (string Hash, byte[]? Content) ComputeHashAndOptionalContent(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var content = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            hasher.AppendData(buffer, 0, read);
            if (totalBytes < MaxRetainedContentBytes)
            {
                var captureLength = (int)Math.Min(read, MaxRetainedContentBytes - totalBytes);
                content.Write(buffer, 0, captureLength);
            }

            totalBytes += read;
        }

        var hash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        return (hash, totalBytes <= MaxRetainedContentBytes ? content.ToArray() : null);
    }

    private bool TryMarkObserved(string key, string observed)
    {
        while (true)
        {
            if (_lastObservedHashes.TryGetValue(key, out var previous))
            {
                if (string.Equals(previous, observed, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (_lastObservedHashes.TryUpdate(key, observed, previous))
                {
                    return true;
                }

                continue;
            }

            if (_lastObservedHashes.TryAdd(key, observed))
            {
                return true;
            }
        }
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path))
        {
            throw new ArgumentException("Path must be workspace-relative.", nameof(path));
        }

        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Path must not contain traversal segments.", nameof(path));
        }

        var normalized = string.Join('/', segments);
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Path must be workspace-relative.", nameof(path));
        }

        return normalized;
    }

    private static string ExpectedKey(string workspaceRoot, string normalizedPath) =>
        $"{Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)}|{normalizedPath}";

    private sealed class WatchRegistration : IDisposable
    {
        private readonly FileSystemWatcher _watcher;
        private readonly FileSystemEventHandler _handler;
        private readonly RenamedEventHandler _renamed;
        private readonly ErrorEventHandler _error;
        private readonly ConcurrentDictionary<string, Timer> _timers;
        private readonly CancellationTokenSource _cancellation;
        private readonly SemaphoreSlim _rescanGate;
        private int _disposed;

        public WatchRegistration(
            FileSystemWatcher watcher,
            FileSystemEventHandler handler,
            RenamedEventHandler renamed,
            ErrorEventHandler error,
            ConcurrentDictionary<string, Timer> timers,
            CancellationTokenSource cancellation,
            SemaphoreSlim rescanGate)
        {
            _watcher = watcher;
            _handler = handler;
            _renamed = renamed;
            _error = error;
            _timers = timers;
            _cancellation = cancellation;
            _rescanGate = rescanGate;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellation.Cancel();
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= _handler;
            _watcher.Created -= _handler;
            _watcher.Deleted -= _handler;
            _watcher.Renamed -= _renamed;
            _watcher.Error -= _error;
            _watcher.Dispose();
            foreach (var pair in _timers)
            {
                pair.Value.Dispose();
                _timers.TryRemove(pair.Key, out _);
            }
            _rescanGate.Dispose();
            _cancellation.Dispose();
        }
    }
}
