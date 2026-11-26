using System.Diagnostics;
using RuAgent.Broker.Tools.Git;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// The fixed read-only Git probe used for Host prompt context. The fake-command
/// cases pin the wire parsing; the real-repository case proves the argv works
/// against Git itself.
/// </summary>
[Collection(nameof(GitProcessHeavyCollection))]
public sealed class WorkspaceGitProbeTests
{
    [Fact]
    public void Status_parsing_counts_each_record_kind_and_samples_paths()
    {
        const string status =
            "# branch.oid abc123\0# branch.head main\0# branch.ab +2 -1\0" +
            "1 .M N... 100644 100644 100644 abc abc src/a.cs\0" +
            "1 M. N... 100644 100644 100644 abc abc src/b.cs\0" +
            "? new.txt\0" +
            "u UU N... 100644 100644 100644 100644 abc abc abc conflict.txt\0" +
            "2 R. N... 100644 100644 100644 abc abc R100 src/c.cs\0src/old.cs\0";

        var sample = WorkspaceGitProbe.ParseStatus(status);

        Assert.Equal(1, sample.Modified);
        Assert.Equal(2, sample.Staged);
        Assert.Equal(1, sample.Untracked);
        Assert.Equal(1, sample.Conflicted);
        Assert.Equal(2, sample.Ahead);
        Assert.Equal(1, sample.Behind);
        Assert.Equal(["src/a.cs", "src/b.cs", "new.txt", "src/c.cs"], sample.Paths);
    }

    [Fact]
    public void A_path_bearing_a_space_is_kept_whole()
    {
        var sample = WorkspaceGitProbe.ParseStatus("1 .M N... 100644 100644 100644 abc abc dir/one two.cs\0");

        Assert.Equal(1, sample.Modified);
        Assert.Equal(["dir/one two.cs"], sample.Paths);
    }

    [Fact]
    public async Task A_workspace_outside_a_repository_is_reported_as_not_a_repository()
    {
        var fake = new FakeGit(_ => new GitCliResult(false, 128, string.Empty, string.Empty, "git_exit_128"));

        var snapshot = await new WorkspaceGitProbe(fake).ReadAsync(@"C:\work\plain", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.False(snapshot!.IsRepository);
    }

    [Fact]
    public async Task An_unavailable_git_reader_yields_no_snapshot()
    {
        var fake = new FakeGit(_ => new GitCliResult(false, -1, string.Empty, string.Empty, "process_start_failed"));

        var snapshot = await new WorkspaceGitProbe(fake).ReadAsync(@"C:\work\plain", CancellationToken.None);

        Assert.Null(snapshot);
    }

    [Fact]
    public async Task A_repository_snapshot_reports_branch_head_and_counts()
    {
        var fake = new FakeGit(argv => argv switch
        {
            _ when argv.Contains("status") => Ok("# branch.ab +1 -0\0? new.txt\0"),
            _ when argv.Contains("symbolic-ref") => Ok("dev\n"),
            _ when argv.Contains("-1") => Ok("abc123\tfix the build\n"),
            _ when argv.Contains("-5") => Ok("abc123\tfix the build\ndef456\tearlier\n"),
            _ when argv.Contains("rev-parse") => Ok("D:\\work\\repo\n"),
            _ => Ok(string.Empty),
        });

        var snapshot = await new WorkspaceGitProbe(fake).ReadAsync(@"D:\work\repo", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.IsRepository);
        Assert.Equal(@"D:\work\repo", snapshot.Root);
        Assert.Equal("dev", snapshot.Branch);
        Assert.Equal("abc123", snapshot.HeadCommit);
        Assert.Equal("fix the build", snapshot.HeadSubject);
        Assert.Equal(1, snapshot.Ahead);
        Assert.Equal(0, snapshot.Behind);
        Assert.Equal(1, snapshot.Untracked);
        Assert.Equal(["new.txt"], snapshot.ChangedPaths);
        Assert.Equal(2, snapshot.RecentCommits.Count);
        Assert.True(snapshot.DescribesWorkingTree);
        Assert.All(fake.Calls, argv => Assert.Contains("core.hooksPath=NUL", argv));
        Assert.All(fake.Environments, environment =>
            Assert.Equal("1", environment!["GIT_CONFIG_NOSYSTEM"]));
    }

    [Fact]
    public async Task A_truncated_status_is_not_read_as_a_clean_tree()
    {
        var fake = new FakeGit(argv => argv.Contains("status")
            ? new GitCliResult(false, -1, string.Empty, string.Empty, "git_output_truncated")
            : argv.Contains("symbolic-ref")
                ? Ok("main\n")
                : Ok(string.Empty));

        var snapshot = await new WorkspaceGitProbe(fake).ReadAsync(@"C:\work\repo", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.Truncated);
        Assert.False(snapshot.StatusUnavailable);
        Assert.False(snapshot.DescribesWorkingTree);
    }

    [Fact]
    public async Task A_failed_status_is_reported_as_unavailable()
    {
        var fake = new FakeGit(argv => argv.Contains("status")
            ? new GitCliResult(false, 1, string.Empty, string.Empty, "git_exit_1")
            : argv.Contains("symbolic-ref")
                ? Ok("main\n")
                : Ok(string.Empty));

        var snapshot = await new WorkspaceGitProbe(fake).ReadAsync(@"C:\work\repo", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.True(snapshot!.StatusUnavailable);
        Assert.False(snapshot.DescribesWorkingTree);
    }

    [Fact]
    public async Task The_real_git_argv_reports_branch_head_and_the_dirty_tree()
    {
        var root = CreateRepository();
        try
        {
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "one");
            CommitAll(root, "initial");
            File.WriteAllText(Path.Combine(root, "tracked.txt"), "two");
            File.WriteAllText(Path.Combine(root, "untracked.txt"), "new");

            var snapshot = await new WorkspaceGitProbe().ReadAsync(root, CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.True(snapshot!.IsRepository);
            Assert.True(snapshot.DescribesWorkingTree);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.HeadCommit));
            Assert.True(snapshot.Modified >= 1);
            Assert.True(snapshot.Untracked >= 1);
            Assert.Contains(snapshot.ChangedPaths, path => path.Contains("tracked.txt", StringComparison.Ordinal));
            Assert.Contains(snapshot.ChangedPaths, path => path.Contains("untracked.txt", StringComparison.Ordinal));
            Assert.Contains(snapshot.RecentCommits, line => line.Contains("initial", StringComparison.Ordinal));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Working_tree_facts_are_scoped_to_the_run_workspace()
    {
        var root = CreateRepository();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "inside"));
            Directory.CreateDirectory(Path.Combine(root, "outside"));
            File.WriteAllText(Path.Combine(root, "inside", "a.txt"), "one");
            File.WriteAllText(Path.Combine(root, "outside", "b.txt"), "one");
            CommitAll(root, "initial");
            File.WriteAllText(Path.Combine(root, "inside", "a.txt"), "two");
            File.WriteAllText(Path.Combine(root, "outside", "b.txt"), "two");

            var snapshot = await new WorkspaceGitProbe()
                .ReadAsync(Path.Combine(root, "inside"), CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Equal(1, snapshot!.Modified);
            Assert.Equal(["a.txt"], snapshot.ChangedPaths);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task An_empty_repository_reports_no_head_and_no_commits()
    {
        var root = CreateRepository();
        try
        {
            var snapshot = await new WorkspaceGitProbe().ReadAsync(root, CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.True(snapshot!.IsRepository);
            Assert.Null(snapshot.HeadCommit);
            Assert.Empty(snapshot.RecentCommits);
            Assert.True(snapshot.DescribesWorkingTree);
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task A_detached_head_reports_no_branch_but_keeps_the_commit()
    {
        var root = CreateRepository();
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "one");
            CommitAll(root, "initial");
            Assert.Equal(0, Git(root, "checkout", "--quiet", "--detach").ExitCode);

            var snapshot = await new WorkspaceGitProbe().ReadAsync(root, CancellationToken.None);

            Assert.NotNull(snapshot);
            Assert.Null(snapshot!.Branch);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.HeadCommit));
        }
        finally
        {
            Delete(root);
        }
    }

    [Fact]
    public async Task Cancellation_reaches_the_probe()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fake = new FakeGit(_ => Ok(string.Empty));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new WorkspaceGitProbe(fake).ReadAsync(@"C:\work\repo", cancellation.Token));
    }

    [Fact]
    public async Task Changed_paths_are_capped()
    {
        var status = string.Concat(Enumerable.Range(0, 25)
            .Select(index => $"1 .M N... 100644 100644 100644 abc abc file{index:D2}.txt\0"));
        var fake = new FakeGit(argv => argv.Contains("status")
            ? Ok(status)
            : argv.Contains("rev-parse")
                ? Ok("C:\\work\\repo\n")
                : argv.Contains("symbolic-ref")
                    ? Ok("main\n")
                    : Ok("abc123\tfix\n"));

        var snapshot = await new WorkspaceGitProbe(fake).ReadAsync(@"C:\work\repo", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal(20, snapshot!.ChangedPaths.Count);
        Assert.Equal(25, snapshot.Modified);
    }

    private static GitCliResult Ok(string stdout) => new(true, 0, stdout, string.Empty, string.Empty);

    private static string CreateRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-workspace-git-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Assert.Equal(0, Git(root, "init", "--quiet").ExitCode);
        Git(root, "config", "user.email", "ru@example.test");
        Git(root, "config", "user.name", "Ru Test");
        return root;
    }

    private static void CommitAll(string root, string message)
    {
        Assert.Equal(0, Git(root, "add", "-A").ExitCode);
        var commit = Git(root, "commit", "--quiet", "-m", message);
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
        catch (IOException)
        {
        }
    }

    private sealed class FakeGit(Func<IReadOnlyList<string>, GitCliResult> handler) : IGitCli
    {
        public List<IReadOnlyList<string>> Calls { get; } = new();

        public List<IReadOnlyDictionary<string, string>?> Environments { get; } = new();

        public Task<GitCliResult> RunAsync(
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken,
            IReadOnlyDictionary<string, string>? additionalEnvironment = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(arguments);
            Environments.Add(additionalEnvironment);
            return Task.FromResult(handler(arguments));
        }
    }
}
