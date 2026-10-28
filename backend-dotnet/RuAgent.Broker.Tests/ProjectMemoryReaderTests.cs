using RuAgent.Broker.Guidance;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class ProjectMemoryReaderTests
{
    [Fact]
    public async Task Reader_rejects_a_symlink_to_a_catalog_protected_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-project-memory-" + Guid.NewGuid().ToString("N"));
        var memoryDirectory = Directory.CreateDirectory(Path.Combine(root, ".ru", "memory"));
        var protectedFile = Path.Combine(root, "runtime_descriptor.json");
        var memoryPath = Path.Combine(memoryDirectory.FullName, "MEMORY.md");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(protectedFile, "protected content");
        try
        {
            try { File.CreateSymbolicLink(memoryPath, protectedFile); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var result = await ProjectMemoryReader.ReadAsync(root);

            Assert.Null(result.Content);
            Assert.Equal("memory_path_unavailable", result.Diagnostic);
        }
        finally
        {
            if (File.Exists(memoryPath)) File.Delete(memoryPath);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Reader_rejects_a_hard_linked_memory_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-project-memory-" + Guid.NewGuid().ToString("N"));
        var memoryDirectory = Directory.CreateDirectory(Path.Combine(root, ".ru", "memory"));
        var originalPath = Path.Combine(root, "original.md");
        var memoryPath = Path.Combine(memoryDirectory.FullName, "MEMORY.md");
        await File.WriteAllTextAsync(originalPath, "linked content");
        try
        {
            if (!FileReadAdapterTests.TryCreateHardLink(memoryPath, originalPath)) return;

            var result = await ProjectMemoryReader.ReadAsync(root);

            Assert.Null(result.Content);
            Assert.Equal("memory_linked_file", result.Diagnostic);
        }
        finally
        {
            if (File.Exists(memoryPath)) File.Delete(memoryPath);
            Directory.Delete(root, recursive: true);
        }
    }
}
