using System.Text;
using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Desktop;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileReadAdapterTests : IDisposable
{
    private const int AllocationCeiling = 1024 * 1024;
    private readonly WorkspacePathGuard _workspace = NewWorkspace();
    private readonly FileTools _files;

    public FileReadAdapterTests() => _files = new FileTools(_workspace);

    [Fact]
    public void Read_allocates_only_the_requested_window_for_a_large_file()
    {
        File.WriteAllText(FullPath("warmup.txt"), "warmup");
        _ = _files.Read("warmup.txt", maxBytes: 16);
        using (var stream = File.Create(FullPath("large.bin")))
        {
            stream.SetLength(16L * 1024 * 1024);
        }

        const int maxBytes = 4 * 1024;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = _files.Read("large.bin", maxBytes);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(result["truncated"]!.GetValue<bool>());
        Assert.Equal(maxBytes, Encoding.UTF8.GetByteCount(result["content"]!.GetValue<string>()));
        Assert.True(
            allocated < AllocationCeiling,
            $"Bounded read allocated {allocated} bytes for a {maxBytes}-byte window.");
    }

    public void Dispose() => Directory.Delete(_workspace.Root, recursive: true);

    private static WorkspacePathGuard NewWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-read-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new WorkspacePathGuard(root);
    }

    private string FullPath(string relative) => Path.Combine(_workspace.Root, relative);
}
