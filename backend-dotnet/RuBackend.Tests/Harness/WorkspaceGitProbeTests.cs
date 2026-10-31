using System.Diagnostics;
using RuBackend.Harness.Prompting;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// The prompt only needs counts, but a wrong count is worse than none: a "clean"
/// claim on a dirty tree would invite the model to overwrite work in progress.
/// These run the real git binary against throwaway repositories.
/// </summary>
public sealed class WorkspaceGitProbeTests
{
    [Fact]
    public void Reports_clean_for_a_committed_repository()
    {
        var repository = CreateRepository();
        try
        {
            Assert.Equal("clean", WorkspaceGitProbe.Describe(repository));
        }
        finally
        {
            Delete(repository);
        }
    }

    [Fact]
    public void Counts_changed_and_untracked_files_without_naming_them()
    {
        var repository = CreateRepository();
        try
        {
            File.WriteAllText(Path.Combine(repository, "a.txt"), "changed");
            File.WriteAllText(Path.Combine(repository, "new.txt"), "untracked");

            var state = WorkspaceGitProbe.Describe(repository);

            Assert.Equal("dirty (1 changed, 1 untracked)", state);
            Assert.DoesNotContain("a.txt", state, StringComparison.Ordinal);
            Assert.DoesNotContain("new.txt", state, StringComparison.Ordinal);
        }
        finally
        {
            Delete(repository);
        }
    }

    [Fact]
    public void Reports_not_a_repository_outside_git()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ru-git-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal("not_a_repository", WorkspaceGitProbe.Describe(directory));
        }
        finally
        {
            Delete(directory);
        }
    }

    private static string CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-git-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Assert.Equal(0, Git(root, "init", "--quiet").ExitCode);
        Git(root, "config", "user.email", "ru@example.test");
        Git(root, "config", "user.name", "Ru Test");
        File.WriteAllText(Path.Combine(root, "a.txt"), "base");
        Assert.Equal(0, Git(root, "add", "a.txt").ExitCode);
        var commit = Git(root, "commit", "-m", "base", "--quiet");
        Assert.True(commit.ExitCode == 0, commit.Stderr);
        return root;
    }

    private static void Delete(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            // git object files are read-only; recursive delete needs them writable.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; leftovers in temp must not fail the test.
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) Git(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("git did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
