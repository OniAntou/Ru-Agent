using System.Diagnostics;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Git;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// A repository can configure a clean filter that Git runs while it computes
/// status. Read-only plan work must never execute that program, even when the
/// adapter prefixes its argv with <c>-c</c>, and the child environment must not
/// hand it Host secrets.
/// </summary>
[Collection(nameof(GitProcessHeavyCollection))]
public sealed class GitFilterHardeningTests
{
    [Fact]
    public async Task Git_status_adapter_does_not_run_a_repo_filter_or_expose_host_secrets()
    {
        const string secretName = "RU_DESKTOP_TOKEN";
        var previous = Environment.GetEnvironmentVariable(secretName);
        var root = CreateRepository();
        Environment.SetEnvironmentVariable(secretName, "fake-token-value");
        try
        {
            ConfigureFilter(root, "evil", "echo \"$RU_DESKTOP_TOKEN\" > marker.txt");
            WriteAttributes(root, "* filter=evil");
            CommitAll(root, "base");
            ModifySameSize(root, "a.txt", "abce");

            // Prove the fixture: direct git status runs the filter and inherits the
            // test process's secret, which is exactly the behavior being blocked.
            Assert.Equal(0, Git(root, "--no-optional-locks", "-c", "core.fsmonitor=false",
                "status", "--porcelain=v2", "--branch").ExitCode);
            var marker = Path.Combine(root, "marker.txt");
            Assert.True(File.Exists(marker), "the fixture must prove the repo filter really runs");
            Assert.Contains("fake-token-value", File.ReadAllText(marker), StringComparison.Ordinal);
            File.Delete(marker);

            ModifySameSize(root, "a.txt", "abcf");
            var report = await new GitStatusAdapter().ExecuteAsync(root, CancellationToken.None);

            Assert.True(report.Succeeded);
            Assert.False(File.Exists(marker), "the Broker read must not execute a repository clean filter");
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, previous);
            Delete(root);
        }
    }

    [Fact]
    public async Task Filter_guard_handles_a_driver_name_with_equals_and_case_duplicates()
    {
        var root = CreateRepository();
        try
        {
            // Two drivers that differ only by case: Ordinal dedupe must keep both,
            // because Git treats the names as distinct.
            ConfigureFilter(root, "Evil", "echo upper > upper-marker.txt");
            ConfigureFilter(root, "evil", "echo lower > lower-marker.txt");
            // A driver whose name contains '=' cannot be overridden through -c.
            ConfigureFilter(root, "a=b", "echo equals > equals-marker.txt");
            File.WriteAllText(Path.Combine(root, "b.txt"), "wxyz");
            WriteAttributes(root, "a.txt filter=Evil\nb.txt filter=a=b");
            CommitAll(root, "base");
            ModifySameSize(root, "a.txt", "abce");
            ModifySameSize(root, "b.txt", "wxyA");

            // The fixture proves both filters run under plain git.
            Assert.Equal(0, Git(root, "--no-optional-locks", "-c", "core.fsmonitor=false",
                "status", "--porcelain=v2", "--branch").ExitCode);
            Assert.True(File.Exists(Path.Combine(root, "upper-marker.txt")));
            Assert.True(File.Exists(Path.Combine(root, "equals-marker.txt")));
            File.Delete(Path.Combine(root, "upper-marker.txt"));
            File.Delete(Path.Combine(root, "equals-marker.txt"));

            var report = await new GitStatusAdapter().ExecuteAsync(root, CancellationToken.None);

            Assert.True(report.Succeeded);
            Assert.False(File.Exists(Path.Combine(root, "upper-marker.txt")));
            Assert.False(File.Exists(Path.Combine(root, "equals-marker.txt")));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Large_status_is_not_reported_as_truncated_or_too_large()
    {
        var root = CreateRepository();
        try
        {
            for (var index = 0; index < 300; index++)
            {
                File.WriteAllText(Path.Combine(root, $"f{index:D3}-{new string('x', 100)}.txt"), "a");
            }

            CommitAll(root, "base");
            for (var index = 0; index < 300; index++)
            {
                File.WriteAllText(Path.Combine(root, $"f{index:D3}-{new string('x', 100)}.txt"), "b");
            }

            var report = await new GitStatusAdapter().ExecuteAsync(root, CancellationToken.None);

            Assert.True(report.Succeeded, report.ErrorCode);
            var output = Assert.IsType<JsonObject>(report.Output);
            Assert.False(output["truncated"]!.GetValue<bool>());
            Assert.Equal(300, output["unstaged"]!.AsArray().Count);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Managed_write_keeps_filters_but_uses_the_sanitized_environment()
    {
        const string secretName = "RU_DESKTOP_TOKEN";
        var previous = Environment.GetEnvironmentVariable(secretName);
        var root = CreateRepository();
        Environment.SetEnvironmentVariable(secretName, "fake-token-value");
        try
        {
            ConfigureFilter(root, "evil", "echo \"$RU_DESKTOP_TOKEN\" > marker.txt");
            WriteAttributes(root, "* filter=evil");
            CommitAll(root, "base");
            ModifySameSize(root, "a.txt", "abce");

            // add is a write verb: the filter must run, but without the Host's env.
            var result = await new StreamingGitCli().RunAsync(["add", "a.txt"], root, CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            var marker = Path.Combine(root, "marker.txt");
            Assert.True(File.Exists(marker), "a write verb must keep its repository filter");
            Assert.DoesNotContain("fake-token-value", File.ReadAllText(marker), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, previous);
            Delete(root);
        }
    }

    private static string CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-git-filter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Assert.Equal(0, Git(root, "init", "--quiet").ExitCode);
        Git(root, "config", "user.email", "ru@example.test");
        Git(root, "config", "user.name", "Ru Test");
        File.WriteAllText(Path.Combine(root, "a.txt"), "abcd");
        return root;
    }

    private static void ConfigureFilter(string root, string driver, string command) =>
        Git(root, "config", $"filter.{driver}.clean", command);

    private static void WriteAttributes(string root, string attributes)
    {
        File.WriteAllText(Path.Combine(root, ".gitattributes"), attributes);
    }

    private static void ModifySameSize(string root, string name, string content) =>
        File.WriteAllText(Path.Combine(root, name), content);

    private static void CommitAll(string root, string message)
    {
        Assert.Equal(0, Git(root, "add", "-A").ExitCode);
        var commit = Git(root, "commit", "-m", message, "--quiet");
        Assert.True(commit.ExitCode == 0, commit.Stderr);
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
            // Best-effort cleanup; leftovers in temp must not fail the test.
        }
    }
}
