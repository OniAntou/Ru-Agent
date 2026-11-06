using System.IO;
using System.Text;
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

/// <summary>The user's instruction files: where they live, how they are saved, and what is refused.</summary>
public sealed class InstructionFileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-instructions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Paths_match_the_files_the_host_reads()
    {
        Assert.Equal(Path.Combine(_root, ".ru", "AGENTS.md"), InstructionFileStore.GlobalPath(_root));
        Assert.Equal(Path.Combine(_root, ".ru", "AGENTS.md"), InstructionFileStore.WorkspacePath(_root));
        Assert.Null(InstructionFileStore.WorkspacePath(null));
        // The limits mirror RuInstructionLoader in RuBackend.
        Assert.Equal(64 * 1024, InstructionFileStore.MaxBytes);
        Assert.Equal(256 * 1024, InstructionFileStore.MaxCombinedBytes);
    }

    [Fact]
    public void Saving_a_new_file_creates_its_folder_and_writes_utf8_without_bom_and_plain_newlines()
    {
        var path = InstructionFileStore.GlobalPath(_root);
        var loaded = InstructionFileStore.Read(path);
        Assert.False(loaded.Exists);

        // A WinUI TextBox returns bare carriage returns.
        Assert.Equal(InstructionSaveOutcome.Saved, InstructionFileStore.Save(loaded, "Trả lời tiếng Việt.\rDùng pytest.\r\n"));

        var bytes = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("Trả lời tiếng Việt.\nDùng pytest.\n", Encoding.UTF8.GetString(bytes));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public void An_edit_made_outside_ru_is_not_overwritten_unless_asked()
    {
        var path = InstructionFileStore.WorkspacePath(_root)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "first");
        var loaded = InstructionFileStore.Read(path);
        File.WriteAllText(path, "changed elsewhere, longer");

        Assert.Equal(InstructionSaveOutcome.ChangedOnDisk, InstructionFileStore.Save(loaded, "mine"));
        Assert.Equal("changed elsewhere, longer", File.ReadAllText(path));
        Assert.Equal(InstructionSaveOutcome.Saved, InstructionFileStore.Save(loaded, "mine", overwrite: true));
        Assert.Equal("mine", File.ReadAllText(path));
    }

    [Fact]
    public void A_file_created_elsewhere_after_loading_counts_as_changed()
    {
        var path = InstructionFileStore.GlobalPath(_root);
        var loaded = InstructionFileStore.Read(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "someone else");

        Assert.Equal(InstructionSaveOutcome.ChangedOnDisk, InstructionFileStore.Save(loaded, "mine"));
    }

    [Fact]
    public void Text_over_the_host_limit_is_refused_by_utf8_size()
    {
        var path = InstructionFileStore.GlobalPath(_root);
        var loaded = InstructionFileStore.Read(path);
        // Three bytes per character: under the limit in characters, over it in bytes.
        var text = new string('ệ', InstructionFileStore.MaxBytes / 3 + 1);

        Assert.True(InstructionFileStore.ByteCount(text) > InstructionFileStore.MaxBytes);
        Assert.Equal(InstructionSaveOutcome.TooLarge, InstructionFileStore.Save(loaded, text));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Other_workspace_sources_are_listed_with_their_presence()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "AGENTS.md"), "shared");

        var sources = InstructionFileStore.OtherWorkspaceSources(_root);

        Assert.Contains(("AGENTS.md", true), sources);
        Assert.Contains(("CONTEXT.md", false), sources);
        Assert.Contains((".ru/CONTEXT.md", false), sources);
        Assert.Empty(InstructionFileStore.OtherWorkspaceSources(null));
    }
}
