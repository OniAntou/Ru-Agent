using System.Diagnostics;
using RuAgent.Harness.Core;
using RuBackend.Harness.Git;
using RuBackend.Harness.Reconciliation;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class WorkspaceStateSnapshotServiceTests
{
    [Fact]
    public async Task Large_live_status_produces_a_fingerprint_that_changes_with_the_workspace()
    {
        var repository = CreateRepository();
        var dbPath = Path.Combine(Path.GetTempPath(), "ru-snapshot-" + Guid.NewGuid().ToString("N") + ".db");
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

            using var store = new SqliteRunGitStateStore(dbPath);
            var service = new WorkspaceStateSnapshotService(store, _ => repository);
            var runId = new RunId("run_" + new string('a', 32));

            var first = await service.ComputeFreshHashAsync(runId);
            File.WriteAllText(Path.Combine(repository, "new-untracked.txt"), "new");
            var second = await service.ComputeFreshHashAsync(runId);

            Assert.NotNull(first);
            Assert.NotNull(second);
            Assert.NotEqual(first, second);
        }
        finally
        {
            TryDelete(dbPath);
            Delete(repository);
        }
    }

    private static string CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-snapshot-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Assert.Equal(0, Git(root, "init", "--quiet").ExitCode);
        Git(root, "config", "user.email", "ru@example.test");
        Git(root, "config", "user.name", "Ru Test");
        File.WriteAllText(Path.Combine(root, "a.txt"), "base");
        Assert.Equal(0, Git(root, "add", "a.txt").ExitCode);
        Assert.Equal(0, Git(root, "commit", "-m", "base", "--quiet").ExitCode);
        return root;
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static void Delete(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(directory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
