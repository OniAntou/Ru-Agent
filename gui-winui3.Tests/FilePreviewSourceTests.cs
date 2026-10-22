using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class FilePreviewSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-preview-tests-" + Guid.NewGuid().ToString("N"));
    public FilePreviewSourceTests() => Directory.CreateDirectory(_root);

    [Theory]
    [InlineData("report.DOCX", FilePreviewKind.Office)]
    [InlineData("slides.pptx", FilePreviewKind.Office)]
    [InlineData("sheet.xlsx", FilePreviewKind.Office)]
    [InlineData("book.pdf", FilePreviewKind.Pdf)]
    [InlineData("photo.png", FilePreviewKind.Image)]
    [InlineData("notes.md", FilePreviewKind.Markdown)]
    [InlineData("app.html", FilePreviewKind.Text)]
    public void Formats_choose_read_only_renderers(string path, FilePreviewKind expected) => Assert.Equal(expected, FilePreviewSource.Classify(path));

    [Fact]
    public void Unsupported_executables_are_refused() => Assert.Throws<NotSupportedException>(() => FilePreviewSource.Classify("program.exe"));

    [Fact]
    public void Corrupt_office_packages_are_refused_before_calling_an_external_renderer()
    {
        using var source = new MemoryStream("not a document"u8.ToArray());
        var failure = Assert.Throws<IOException>(() => FilePreviewSource.ValidateOfficePackage(source, "broken.docx"));
        Assert.StartsWith("preview_invalid_document", failure.Message);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void An_explicit_file_click_reads_a_stable_handle_without_allowing_writes()
    {
        string path = Path.Combine(_root, "sample.txt");
        File.WriteAllText(path, "read-only preview");
        using var source = FilePreviewSource.Open(_root, path);
        Assert.True(source.CanRead);
        Assert.False(source.CanWrite);
        Assert.Throws<IOException>(() => File.WriteAllText(path, "mutated"));
        Assert.Equal("read-only preview", new StreamReader(source).ReadToEnd());
    }

    [Fact]
    public void Outside_paths_hidden_files_and_oversized_text_are_refused()
    {
        Assert.Throws<UnauthorizedAccessException>(() => FilePreviewSource.Open(_root, Path.Combine(Path.GetDirectoryName(_root)!, "outside.txt")));
        File.WriteAllText(Path.Combine(_root, ".env"), "synthetic secret");
        Assert.Throws<UnauthorizedAccessException>(() => FilePreviewSource.Open(_root, Path.Combine(_root, ".env")));
        string large = Path.Combine(_root, "large.txt");
        using (var file = File.Create(large)) file.SetLength(FilePreviewSource.MaximumTextBytes + 1);
        var failure = Assert.Throws<IOException>(() => FilePreviewSource.Open(_root, large));
        Assert.StartsWith("preview_too_large", failure.Message);
    }

    [Fact]
    public void A_directory_junction_cannot_redirect_a_preview_read()
    {
        string actual = Path.Combine(_root, "actual");
        Directory.CreateDirectory(actual);
        File.WriteAllText(Path.Combine(actual, "sample.txt"), "synthetic");
        string link = Path.Combine(_root, "redirect");
        // Junction creation does not require Windows developer-mode symlink privileges.
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-Command", $"New-Item -ItemType Junction -Path '{link}' -Target '{actual}' | Out-Null" },
        })!;
        Assert.True(process.WaitForExit(10000));
        Assert.Equal(0, process.ExitCode);
        Assert.Throws<UnauthorizedAccessException>(() => FilePreviewSource.Open(_root, Path.Combine(link, "sample.txt")));
    }

    public void Dispose()
    {
        // Remove the owned junction before its target. Recursive enumeration can
        // otherwise delete the target first and leave a dangling junction.
        string link = Path.Combine(_root, "redirect");
        if (Directory.Exists(link)) Directory.Delete(link, recursive: false);
        Directory.Delete(_root, recursive: true);
    }
}
