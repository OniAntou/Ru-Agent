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
}
