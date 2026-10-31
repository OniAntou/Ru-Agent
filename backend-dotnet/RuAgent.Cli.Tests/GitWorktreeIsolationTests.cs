using System.Diagnostics;
using RuAgent.Cli.Worktree;
using Xunit;

namespace RuAgent.Cli.Tests;

/// <summary>
/// These drive the real git binary against a throwaway repository: a worktree is
/// a git-level object, so a fake would prove nothing. Each test removes its
/// worktree and repository before finishing.
/// </summary>
public sealed class GitWorktreeIsolationTests
{
    [Fact]
    public async Task Creates_an_isolated_worktree_at_head()
    {
        var repository = CreateRepository();
        var worktreeRoot = NewDirectory("ru-worktree-root-");
        try
        {
            var (lease, error) = await GitWorktreeIsolation.CreateAsync(
                repository, worktreeRoot, CancellationToken.None);

            Assert.Null(error);
            Assert.NotNull(lease);
            Assert.True(Directory.Exists(lease!.WorktreePath));
            Assert.Equal("base", File.ReadAllText(Path.Combine(lease.WorktreePath, "a.txt")));
            Assert.Equal(Path.Combine(lease.WorktreePath, "a.txt"), Path.Combine(lease.WorkspacePath, "a.txt"));
            Assert.StartsWith("ru/worktree/", lease.Branch, StringComparison.Ordinal);
            Assert.Contains(lease.Branch, RunGit(repository, "branch", "--list").Stdout, StringComparison.Ordinal);

            // The copy is isolated in both directions.
            File.WriteAllText(Path.Combine(lease.WorktreePath, "a.txt"), "changed");
            Assert.Equal("base", File.ReadAllText(Path.Combine(repository, "a.txt")));

            RemoveWorktree(repository, lease.WorktreePath);
        }
        finally
        {
            Delete(repository);
            Delete(worktreeRoot);
        }
    }

    [Fact]
    public async Task Non_repository_is_a_named_failure()
    {
        var directory = NewDirectory("ru-worktree-plain-");
        try
        {
            var (lease, error) = await GitWorktreeIsolation.CreateAsync(
                directory, directory, CancellationToken.None);

            Assert.Null(lease);
            Assert.StartsWith("worktree_not_repository", error, StringComparison.Ordinal);
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Missing_workspace_is_a_named_failure()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ru-worktree-missing-" + Guid.NewGuid().ToString("N"));

        var (lease, error) = await GitWorktreeIsolation.CreateAsync(
            missing, Path.GetTempPath(), CancellationToken.None);

        Assert.Null(lease);
        Assert.StartsWith("worktree_workspace_missing", error, StringComparison.Ordinal);
    }

    private static string CreateRepository()
    {
        var root = NewDirectory("ru-worktree-src-");
        Assert.Equal(0, RunGit(root, "init", "--quiet").ExitCode);
        RunGit(root, "config", "user.email", "ru@example.test");
        RunGit(root, "config", "user.name", "Ru Test");
        File.WriteAllText(Path.Combine(root, "a.txt"), "base");
        Assert.Equal(0, RunGit(root, "add", "a.txt").ExitCode);
        var commit = RunGit(root, "commit", "-m", "base", "--quiet");
        Assert.True(commit.ExitCode == 0, commit.Stderr);
        return root;
    }

    private static string NewDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void RemoveWorktree(string repository, string worktreePath)
    {
        var result = RunGit(repository, "worktree", "remove", "--force", worktreePath);
        Assert.True(result.ExitCode == 0, result.Stderr);
    }

    private static void Delete(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            // git writes read-only object files; recursive delete fails on those
            // unless the attribute is cleared first.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup; a leftover temp directory must not fail the test.
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunGit(string workingDirectory, params string[] arguments)
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
