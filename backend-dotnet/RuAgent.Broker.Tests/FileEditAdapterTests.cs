using RuAgent.Broker.Tools;
using RuAgent.Broker.Tools.Desktop;
using Xunit;

namespace RuAgent.Broker.Tests;

public sealed class FileEditAdapterTests : IDisposable
{
    private readonly WorkspacePathGuard _workspace = NewWorkspace();
    private readonly FileTools _files;

    public FileEditAdapterTests() => _files = new FileTools(_workspace);

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Edit_rejects_when_match_count_differs_from_expected(int expectedOccurrences)
    {
        WriteFile("a.txt", "x\ny\nx\n");

        var error = Assert.Throws<FileEditException>(
            () => _files.Edit("a.txt", "x", "z", expectedOccurrences));

        Assert.Equal("edit_not_unique", error.Code);
        Assert.Equal(2, error.Details?["matches"]?.GetValue<int>());
    }

    [Fact]
    public void Edit_replaces_single_match_and_writes_utf8_no_bom()
    {
        WriteFile("a.txt", "hello world");

        var result = _files.Edit("a.txt", "world", "Ru");

        Assert.Equal("a.txt", result["path"]?.GetValue<string>());
        Assert.Equal(1, result["replacements"]?.GetValue<int>());
        Assert.Equal("hello Ru", ReadFile("a.txt"));
        Assert.False(HasUtf8Bom("a.txt"));
    }

    [Fact]
    public void Edit_replaces_both_matches_when_expected_occurrences_is_two()
    {
        WriteFile("a.txt", "x\ny\nx\n");

        var result = _files.Edit("a.txt", "x", "z", expectedOccurrences: 2);

        Assert.Equal(2, result["replacements"]?.GetValue<int>());
        Assert.Equal("z\ny\nz\n", ReadFile("a.txt"));
    }

    [Fact]
    public void Edit_rejects_empty_old_string_without_touching_the_file()
    {
        WriteFile("a.txt", "hello");

        var error = Assert.Throws<FileEditException>(() => _files.Edit("a.txt", "", "z"));

        Assert.Equal("old_string_empty", error.Code);
        Assert.Equal("hello", ReadFile("a.txt"));
    }

    [Fact]
    public void Edit_rejects_a_missing_file()
    {
        var error = Assert.Throws<FileEditException>(
            () => _files.Edit("missing.txt", "x", "z"));

        Assert.Equal("file_not_found", error.Code);
    }

    [Fact]
    public void Edit_rejects_files_larger_than_four_megabytes()
    {
        using (var stream = File.Create(FullPath("big.bin")))
        {
            var megabyte = new byte[1024 * 1024];
            Array.Fill(megabyte, (byte)'a');
            for (var i = 0; i < 4; i++)
            {
                stream.Write(megabyte);
            }

            stream.WriteByte((byte)'a');
        }

        var error = Assert.Throws<FileEditException>(() => _files.Edit("big.bin", "b", "z"));

        Assert.Equal("file_too_large", error.Code);
    }

    public void Dispose() => Directory.Delete(_workspace.Root, recursive: true);

    private static WorkspacePathGuard NewWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new WorkspacePathGuard(root);
    }

    private string FullPath(string relative) => Path.Combine(_workspace.Root, relative);

    private void WriteFile(string relative, string content) => File.WriteAllText(FullPath(relative), content);

    private string ReadFile(string relative) => File.ReadAllText(FullPath(relative));

    private bool HasUtf8Bom(string relative)
    {
        var bytes = File.ReadAllBytes(FullPath(relative));
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
    }
}
