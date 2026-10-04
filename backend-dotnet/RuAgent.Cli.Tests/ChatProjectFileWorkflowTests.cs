using RuAgent.Cli;
using System.Runtime.InteropServices;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class ChatProjectFileWorkflowTests
{
    [Fact]
    public void FindCandidates_FiltersPrivatePathsAndAppliesBoundedSearch()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "secrets"));
        File.WriteAllText(Path.Combine(root, "src", "compiler.md"), "safe");
        File.WriteAllText(Path.Combine(root, ".env.production"), "secret");
        File.WriteAllText(Path.Combine(root, "secrets", "token.txt"), "secret");

        try
        {
            var choices = ChatProjectFileWorkflow.FindCandidates(root, "compiler", CancellationToken.None);

            var choice = Assert.Single(choices);
            Assert.Equal(Path.Combine("src", "compiler.md"), choice.RelativePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadSelectedAsync_ReadsContainedFileAndRejectsOutsidePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectFile = Path.Combine(root, "notes.md");
        var outsideFile = Path.Combine(Path.GetTempPath(), "ru-outside-" + Guid.NewGuid().ToString("N") + ".md");
        await File.WriteAllTextAsync(projectFile, "inside");
        await File.WriteAllTextAsync(outsideFile, "outside");

        try
        {
            var loaded = await ChatProjectFileWorkflow.LoadSelectedAsync(projectFile, root, CancellationToken.None);
            var denied = await ChatProjectFileWorkflow.LoadSelectedAsync(outsideFile, root, CancellationToken.None);
            var alternateRoot = ToggleCase(root);
            var casingVariant = loaded;
            if (Directory.Exists(alternateRoot))
            {
                var alternateCandidates = ChatProjectFileWorkflow.FindCandidates(alternateRoot, "notes", CancellationToken.None);
                casingVariant = await ChatProjectFileWorkflow.LoadSelectedAsync(
                    Assert.Single(alternateCandidates).Path,
                    alternateRoot,
                    CancellationToken.None);
            }

            Assert.Equal("inside", loaded.Text?.TextContent);
            Assert.Contains("project_file_denied", denied.Error, StringComparison.Ordinal);
            Assert.Equal("inside", casingVariant.Text?.TextContent);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            File.Delete(outsideFile);
        }
    }

    private static string ToggleCase(string value) => new(value.Select(character =>
        char.IsUpper(character) ? char.ToLowerInvariant(character)
        : char.IsLower(character) ? char.ToUpperInvariant(character)
        : character).ToArray());

    [Fact]
    public async Task LoadSelectedAsync_RejectsFilesWithMultipleHardLinks()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var projectFile = Path.Combine(root, "notes.md");
        var secondLink = Path.Combine(root, "notes-copy.md");
        await File.WriteAllTextAsync(projectFile, "inside");

        try
        {
            try
            {
                if (!TryCreateHardLink(secondLink, projectFile))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return;
            }

            var loaded = await ChatProjectFileWorkflow.LoadSelectedAsync(projectFile, root, CancellationToken.None);

            Assert.Null(loaded.Text);
            Assert.Contains("project_file_denied", loaded.Error, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static bool TryCreateHardLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return CreateHardLinkW(linkPath, targetPath, IntPtr.Zero);
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);
}
