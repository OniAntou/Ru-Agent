using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class WorkspaceExternalChangeMonitorTests
{
    [Fact]
    public void Observe_emits_unknown_external_change_only_when_hash_changes()
    {
        var monitor = new WorkspaceExternalChangeMonitor();
        var unchanged = monitor.Observe("src/app.cs", new string('a', 64), new string('a', 64));
        var changed = monitor.Observe("src/app.cs", new string('a', 64), new string('b', 64));

        Assert.Null(unchanged);
        Assert.NotNull(changed);
        Assert.Equal("workspace_external_change_detected", changed!.Kind);
        Assert.Equal("unknown_external", changed.WriterKind);
    }

    [Fact]
    public async Task File_watcher_debounces_and_reports_authoritative_hash_change()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "app.txt");
            await File.WriteAllTextAsync(path, "before");
            var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
            var signal = new TaskCompletionSource<WorkspaceExternalChangeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            var monitor = new WorkspaceExternalChangeMonitor();
            monitor.RegisterExpectedHash("app.txt", expected);
            using var watching = monitor.StartWatching(root, change => signal.TrySetResult(change), TimeSpan.FromMilliseconds(50));

            await File.WriteAllTextAsync(path, "after");
            var result = await signal.Task.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal("app.txt", result.NormalizedPath);
            Assert.Equal("unknown_external", result.WriterKind);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Independent_workspace_watchers_report_same_relative_path()
    {
        var rootOne = Path.Combine(Path.GetTempPath(), "ru-workspace-watch", Guid.NewGuid().ToString("N"));
        var rootTwo = Path.Combine(Path.GetTempPath(), "ru-workspace-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootOne);
        Directory.CreateDirectory(rootTwo);
        try
        {
            var pathOne = Path.Combine(rootOne, "app.txt");
            var pathTwo = Path.Combine(rootTwo, "app.txt");
            await File.WriteAllTextAsync(pathOne, "before-one");
            await File.WriteAllTextAsync(pathTwo, "before-two");
            var monitor = new WorkspaceExternalChangeMonitor();
            monitor.RegisterExpectedHash(rootOne, "app.txt", Hash(pathOne));
            monitor.RegisterExpectedHash(rootTwo, "app.txt", Hash(pathTwo));
            var signals = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var seen = 0;
            using var watchOne = monitor.StartWatching(rootOne, _ =>
            {
                if (Interlocked.Increment(ref seen) == 2)
                {
                    signals.TrySetResult(true);
                }
            }, TimeSpan.FromMilliseconds(50));
            using var watchTwo = monitor.StartWatching(rootTwo, _ =>
            {
                if (Interlocked.Increment(ref seen) == 2)
                {
                    signals.TrySetResult(true);
                }
            }, TimeSpan.FromMilliseconds(50));

            await File.WriteAllTextAsync(pathOne, "after-one");
            await File.WriteAllTextAsync(pathTwo, "after-two");

            Assert.True(await signals.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            if (Directory.Exists(rootOne)) Directory.Delete(rootOne, recursive: true);
            if (Directory.Exists(rootTwo)) Directory.Delete(rootTwo, recursive: true);
        }
    }

    [Fact]
    public async Task Multiple_watchers_for_one_workspace_do_not_duplicate_a_change()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "app.txt");
            await File.WriteAllTextAsync(path, "before");
            var monitor = new WorkspaceExternalChangeMonitor();
            monitor.RegisterExpectedHash(root, "app.txt", Hash(path));
            var firstSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbackCount = 0;
            void OnChange(WorkspaceExternalChangeEvent _)
            {
                if (Interlocked.Increment(ref callbackCount) == 1)
                {
                    firstSignal.TrySetResult(true);
                }
            }

            using var watchOne = monitor.StartWatching(root, OnChange, TimeSpan.FromMilliseconds(50));
            using var watchTwo = monitor.StartWatching(root, OnChange, TimeSpan.FromMilliseconds(50));
            await File.WriteAllTextAsync(path, "after");

            Assert.True(await firstSignal.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            await Task.Delay(250);
            Assert.Equal(1, Volatile.Read(ref callbackCount));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Updating_an_expected_hash_drops_stale_restore_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "app.txt");
            await File.WriteAllTextAsync(path, "before");
            var monitor = new WorkspaceExternalChangeMonitor();
            Assert.Equal(1, monitor.CaptureExpectedHashes(root));
            Assert.True(monitor.TryGetExpectedContent(root, "app.txt", out var content));
            Assert.Equal("before", System.Text.Encoding.UTF8.GetString(content!));

            monitor.RegisterExpectedHash(root, "app.txt", new string('a', 64));

            Assert.False(monitor.TryGetExpectedContent(root, "app.txt", out _));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Capturing_a_workspace_replaces_stale_baseline_entries()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "app.txt");
            await File.WriteAllTextAsync(path, "before");
            var monitor = new WorkspaceExternalChangeMonitor();

            Assert.Equal(1, monitor.CaptureExpectedHashes(root));
            File.Delete(path);
            Assert.Equal(0, monitor.CaptureExpectedHashes(root));

            Assert.False(monitor.TryGetExpectedContent(root, "app.txt", out _));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Updating_expected_hash_resets_observation_deduplication()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "app.txt");
            await File.WriteAllTextAsync(path, "before");
            var monitor = new WorkspaceExternalChangeMonitor();
            monitor.RegisterExpectedHash(root, "app.txt", Hash(path));
            var signals = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var callbackCount = 0;
            using var watching = monitor.StartWatching(root, _ =>
            {
                if (Interlocked.Increment(ref callbackCount) == 2)
                {
                    signals.TrySetResult(true);
                }
            }, TimeSpan.FromMilliseconds(50));

            await File.WriteAllTextAsync(path, "after");
            var firstDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            while (Volatile.Read(ref callbackCount) < 1 && DateTime.UtcNow < firstDeadline)
            {
                await Task.Delay(25);
            }

            Assert.Equal(1, Volatile.Read(ref callbackCount));

            monitor.RegisterExpectedHash(root, "app.txt", new string('a', 64));
            File.Delete(path);
            await File.WriteAllTextAsync(path, "after");

            Assert.True(await signals.Task.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(2, Volatile.Read(ref callbackCount));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Debounce_reads_the_latest_expected_hash_before_reporting()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-watch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "app.txt");
            await File.WriteAllTextAsync(path, "before");
            var monitor = new WorkspaceExternalChangeMonitor();
            monitor.RegisterExpectedHash(root, "app.txt", Hash(path));
            var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var watching = monitor.StartWatchingAsync(
                root,
                _ =>
                {
                    signal.TrySetResult(true);
                    return Task.CompletedTask;
                },
                TimeSpan.FromMilliseconds(300));

            await File.WriteAllTextAsync(path, "after");
            var updatedHash = Hash(path);
            monitor.RegisterExpectedHash(root, "app.txt", updatedHash);

            await Task.Delay(650);
            Assert.False(signal.Task.IsCompleted);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(string path) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
}
