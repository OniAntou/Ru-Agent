extern alias Broker;

using System.Diagnostics;
using Broker::RuAgent.Broker.Tools.Git;
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
    public async Task Reports_clean_for_a_committed_repository()
    {
        var repository = CreateRepository();
        try
        {
            Assert.Equal("clean", await WorkspaceGitProbe.DescribeAsync(repository));
        }
        finally
        {
            Delete(repository);
        }
    }

    [Fact]
    public async Task Counts_changed_and_untracked_files_without_naming_them()
    {
        var repository = CreateRepository();
        try
        {
            File.WriteAllText(Path.Combine(repository, "a.txt"), "changed");
            File.WriteAllText(Path.Combine(repository, "new.txt"), "untracked");

            var state = await WorkspaceGitProbe.DescribeAsync(repository);

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
    public async Task Reports_not_a_repository_outside_git()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ru-git-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal("not_a_repository", await WorkspaceGitProbe.DescribeAsync(directory));
        }
        finally
        {
            Delete(directory);
        }
    }

    [Fact]
    public async Task Large_dirty_tree_reports_counts_not_unknown()
    {
        var repository = CreateRepository();
        try
        {
            for (var index = 0; index < 300; index++)
            {
                File.WriteAllText(Path.Combine(repository, $"f{index:D3}-{new string('x', 100)}.txt"), "a");
            }

            Assert.Equal(0, Git(repository, "add", "-A").ExitCode);
            Assert.Equal(0, Git(repository, "commit", "-m", "bulk", "--quiet").ExitCode);
            for (var index = 0; index < 300; index++)
            {
                File.WriteAllText(Path.Combine(repository, $"f{index:D3}-{new string('x', 100)}.txt"), "b");
            }

            Assert.Equal("dirty (300 changed, 0 untracked)", await WorkspaceGitProbe.DescribeAsync(repository));
        }
        finally
        {
            Delete(repository);
        }
    }

    [Fact]
    public async Task Timeout_is_unknown_not_not_a_repository()
    {
        var state = await WorkspaceGitProbe.DescribeAsync(
            Path.GetTempPath(),
            new FakeGit(new GitCliResult(false, -1, string.Empty, string.Empty, "git_timed_out")),
            CancellationToken.None);

        Assert.Equal("unknown", state);
    }

    [Fact]
    public async Task A_real_non_repository_is_not_a_repository()
    {
        var state = await WorkspaceGitProbe.DescribeAsync(
            Path.GetTempPath(),
            new FakeGit(new GitCliResult(
                false, 128, string.Empty, "fatal: not a git repository (or any of the parent directories): .git",
                "git_exit_128")),
            CancellationToken.None);

        Assert.Equal("not_a_repository", state);
    }

    private sealed class FakeGit(GitCliResult result) : IGitCli
    {
        public Task<GitCliResult> RunAsync(
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? additionalEnvironment = null) =>
            Task.FromResult(result);
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
