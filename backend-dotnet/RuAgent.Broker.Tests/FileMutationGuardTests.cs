using System.Text;
using RuAgent.Broker.Tools.Files;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileMutationGuardTests : IDisposable
{
    private readonly string _root;

    public FileMutationGuardTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ru-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Full(string name) => Path.Combine(_root, name);

    [Fact]
    public void Replace_fails_closed_while_another_writer_holds_the_file()
    {
        // The writer handle is open for the whole guard call: this is the window
        // between the preimage hash and the replacement. The guard must refuse
        // instead of silently overwriting the writer's content.
        var path = Full("target.txt");
        File.WriteAllText(path, "v1");
        var expected = FileHashService.ComputeFileHash(path);
        var newBytes = Encoding.UTF8.GetBytes("v2");

        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            writer.Write(Encoding.UTF8.GetBytes("external"));
            writer.Flush();

            var error = Assert.Throws<UnauthorizedAccessException>(
                () => FileMutationGuard.ReplaceIfHashMatches(path, newBytes, expected));
            Assert.Equal("workspace_changed", error.Message);
        }

        Assert.Equal("external", File.ReadAllText(path));

        var externalHash = FileHashService.ComputeFileHash(path);
        FileMutationGuard.ReplaceIfHashMatches(path, newBytes, externalHash);
        Assert.Equal("v2", File.ReadAllText(path));
    }

    [Fact]
    public void Replace_detects_a_genuine_preimage_change_and_writes_nothing()
    {
        var path = Full("changed.txt");
        File.WriteAllText(path, "v1");
        var expected = FileHashService.ComputeFileHash(path);
        File.WriteAllText(path, "external");

        var error = Assert.Throws<UnauthorizedAccessException>(
            () => FileMutationGuard.ReplaceIfHashMatches(path, Encoding.UTF8.GetBytes("v2"), expected));
        Assert.Equal("workspace_changed", error.Message);
        Assert.Equal("external", File.ReadAllText(path));
    }

    [Fact]
    public void Delete_fails_closed_while_another_writer_holds_the_file()
    {
        var path = Full("delete.txt");
        File.WriteAllText(path, "v1");
        var expected = FileHashService.ComputeFileHash(path);

        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var error = Assert.Throws<UnauthorizedAccessException>(
                () => FileMutationGuard.DeleteIfHashMatches(path, expected));
            Assert.Equal("workspace_changed", error.Message);
        }

        Assert.True(File.Exists(path));
        FileMutationGuard.DeleteIfHashMatches(path, expected);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Replace_with_missing_expected_creates_or_fails_without_clobbering()
    {
        var path = Full("create.txt");
        FileMutationGuard.ReplaceIfHashMatches(path, Encoding.UTF8.GetBytes("created"), FileHashService.Missing);
        Assert.Equal("created", File.ReadAllText(path));

        File.WriteAllText(path, "raced");
        var error = Assert.Throws<UnauthorizedAccessException>(
            () => FileMutationGuard.ReplaceIfHashMatches(path, Encoding.UTF8.GetBytes("clobber"), FileHashService.Missing));
        Assert.Equal("workspace_changed", error.Message);
        Assert.Equal("raced", File.ReadAllText(path));
    }

    [Fact]
    public void Delete_with_missing_expected_does_not_remove_a_concurrently_created_file()
    {
        var path = Full("appeared.txt");
        File.WriteAllText(path, "appeared");

        var error = Assert.Throws<UnauthorizedAccessException>(
            () => FileMutationGuard.DeleteIfHashMatches(path, FileHashService.Missing));
        Assert.Equal("workspace_changed", error.Message);
        Assert.Equal("appeared", File.ReadAllText(path));
    }
}
