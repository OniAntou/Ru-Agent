using System.Text;
using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class WorkspaceWatchRegistryTests
{
    [Fact]
    public async Task Known_file_write_is_journaled_before_return_and_late_watcher_event_does_not_repeat_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-watch-known-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "app.txt");
            await File.WriteAllTextAsync(path, "original");
            var monitor = new WorkspaceExternalChangeMonitor();
            var registry = new WorkspaceWatchRegistry(monitor);
            var changes = new System.Collections.Concurrent.ConcurrentQueue<WorkspaceExternalChangeEvent>();
            using var lease = registry.Acquire(root, change => { changes.Enqueue(change); return Task.FromResult(true); });
            await File.WriteAllTextAsync(path, "tool edit");
            await registry.ObserveKnownPathAsync(root, "app.txt", default);
            Assert.Single(changes);
            await Task.Delay(300); // Allow the OS event's normal debounce callback to arrive.
            Assert.Single(changes);
            await File.WriteAllTextAsync(path, "later external edit");
            await WaitForAsync(() => changes.Count == 2);
            Assert.Equal(2, changes.Count);
            Assert.NotEqual(changes.First().ObservedSha256, changes.Last().ObservedSha256);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Failed_known_write_journaling_does_not_acknowledge_the_new_baseline()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-watch-rejected-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "app.txt");
            await File.WriteAllTextAsync(path, "original");
            var monitor = new WorkspaceExternalChangeMonitor();
            var registry = new WorkspaceWatchRegistry(monitor);
            using var lease = registry.Acquire(root, _ => Task.FromResult(false));
            await File.WriteAllTextAsync(path, "tool edit");
            var error = await Assert.ThrowsAsync<RuAgent.Harness.Persistence.HarnessStoreException>(() =>
                registry.ObserveKnownPathAsync(root, "app.txt", default));
            Assert.Equal("workspace_change_store_failed", error.Code);
            Assert.True(monitor.TryGetExpectedContent(root, "app.txt", out var bytes));
            Assert.Equal("original", Encoding.UTF8.GetString(bytes!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Peer_leases_share_original_content_until_the_last_release_then_recapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-watch-lease-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "app.txt");
            File.WriteAllText(path, "original");
            var monitor = new WorkspaceExternalChangeMonitor();
            var registry = new WorkspaceWatchRegistry(monitor);
            using var first = registry.Acquire(root, _ => Task.FromResult(false));
            File.WriteAllText(path, "external edit");
            using var second = registry.Acquire(root + Path.DirectorySeparatorChar, _ => Task.FromResult(false));
            Assert.True(monitor.TryGetExpectedContent(root, "app.txt", out var original));
            Assert.Equal("original", Encoding.UTF8.GetString(original!));

            first.Dispose();
            first.Dispose();
            Assert.True(monitor.TryGetExpectedContent(root, "app.txt", out _));
            second.Dispose();
            Assert.False(monitor.TryGetExpectedContent(root, "app.txt", out _));

            using var replacement = registry.Acquire(root, _ => Task.FromResult(false));
            Assert.True(monitor.TryGetExpectedContent(root, "app.txt", out var updated));
            Assert.Equal("external edit", Encoding.UTF8.GetString(updated!));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Change_that_failed_to_persist_is_reported_again_for_identical_content()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-watch-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "app.txt");
            File.WriteAllText(path, "original");
            var monitor = new WorkspaceExternalChangeMonitor();
            var registry = new WorkspaceWatchRegistry(monitor);
            var calls = 0;
            using var lease = registry.Acquire(root, _ => Task.FromResult(Interlocked.Increment(ref calls) > 1));

            File.WriteAllText(path, "external edit");
            await WaitForAsync(() => Volatile.Read(ref calls) >= 1);
            await Task.Delay(300);
            File.WriteAllText(path, "external edit");
            await WaitForAsync(() => Volatile.Read(ref calls) >= 2);

            Assert.True(Volatile.Read(ref calls) >= 2);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
    }

    [Fact]
    public async Task Concurrent_acquisitions_keep_the_baseline_until_every_lease_is_released()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-watch-concurrent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "app.txt"), "original");
            var monitor = new WorkspaceExternalChangeMonitor();
            var registry = new WorkspaceWatchRegistry(monitor);
            var leases = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
                Task.Run(() => registry.Acquire(root, _ => Task.FromResult(false)))));
            try
            {
                await Task.WhenAll(leases.Take(15).Select(lease => Task.Run(lease.Dispose)));
                Assert.True(monitor.TryGetExpectedContent(root, "app.txt", out _));
            }
            finally { foreach (var lease in leases) lease.Dispose(); }
            Assert.False(monitor.TryGetExpectedContent(root, "app.txt", out _));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
