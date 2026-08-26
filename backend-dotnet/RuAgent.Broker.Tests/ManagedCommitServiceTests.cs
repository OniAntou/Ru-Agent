using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RuAgent.Broker.Tools.Git;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// Task 7 phase 3: the managed local-commit workflow against disposable repositories.
/// The workflow must only ever stage run-attributed paths, revalidate everything after
/// approval, preserve unrelated staged/unstaged/untracked work, and route every
/// ambiguous failure to explicit reconciliation instead of automatic resets.
/// </summary>
[Collection(nameof(GitProcessHeavyCollection))]
public sealed class ManagedCommitServiceTests
{
    private const string SeedPath = "seed.txt";
    private const string UnrelatedTrackedPath = "other.txt";

    private static readonly IGitCli Cli = new StreamingGitCli();
    private static readonly GitRepositoryLocator Locator = new(Cli);

    [Theory]
    [InlineData(new[] { "commit", "-m", "x" }, true)]
    [InlineData(new[] { "--no-optional-locks", "commit" }, true)]
    [InlineData(new[] { "-c", "user.name=x", "commit" }, true)]
    [InlineData(new[] { "status" }, false)]
    [InlineData(new[] { "committer" }, false)]
    [InlineData(new string[] { }, false)]
    public void Commit_invocations_are_recognized_for_managed_interception(string[] arguments, bool expected)
    {
        Assert.Equal(expected, ManagedCommitService.IsManagedCommitInvocation("git", arguments));
        Assert.False(ManagedCommitService.IsManagedCommitInvocation("rg", arguments));
    }

    [Fact]
    public async Task Preview_builds_an_exact_plan_without_touching_the_real_index()
    {
        var root = CreateRepo();
        try
        {
            Write(root, SeedPath, "attributed change\n");
            var before = IndexFileBytes(root);

            var result = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Null(result.RefusalCode);
            var plan = result.Plan!;
            Assert.Equal(new[] { SeedPath }, plan.StagedPaths);
            Assert.Matches("^[0-9a-f]{40}$", plan.HeadOid);
            Assert.False(string.IsNullOrWhiteSpace(plan.BranchName));
            Assert.Matches("^[0-9a-f]{64}$", plan.DiffHash);
            Assert.True(plan.ObservedPathHashes.ContainsKey(SeedPath));

            Assert.Equal(before, IndexFileBytes(root));

            var again = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);
            Assert.Equal(plan.DiffHash, again.Plan!.DiffHash);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Preview_excludes_dirty_at_start_paths_without_proof()
    {
        var root = CreateRepo();
        try
        {
            Write(root, SeedPath, "dirty before the run started\n");

            var result = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                new[] { SeedPath },
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("nothing_attributed", result.RefusalCode);
            Assert.Null(result.Plan);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Preview_refuses_a_non_empty_start_index()
    {
        var root = CreateRepo();
        try
        {
            Write(root, "staged-by-user.txt", "staged outside the run\n");
            RunGit(root, "add", ".");

            var result = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("index_not_empty", result.RefusalCode);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Preview_refuses_a_detached_head()
    {
        var root = CreateRepo();
        try
        {
            RunGit(root, "checkout", "--detach");

            var result = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("no_branch", result.RefusalCode);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Execute_commits_only_approved_paths_and_preserves_unrelated_work()
    {
        var root = CreateRepo();
        try
        {
            Write(root, SeedPath, "attributed change\n");
            Write(root, UnrelatedTrackedPath, "unrelated unstaged change\n");
            Write(root, "untracked.txt", "unrelated untracked\n");
            var headBefore = HeadOid(root);

            var preview = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);
            var outcome = await Service().ExecuteApprovedAsync(preview.Plan!, "managed commit", CancellationToken.None);

            Assert.Equal(ManagedCommitPhase.Committed, outcome.Phase);
            Assert.NotEqual(headBefore, outcome.NewHeadOid);
            Assert.Equal(headBefore, ParentOid(root));
            var committedFiles = DiffNameStatus(root, "HEAD^", "HEAD");
            Assert.Contains(committedFiles, line => line.EndsWith(SeedPath, StringComparison.Ordinal));
            Assert.DoesNotContain(committedFiles, line => line.EndsWith(UnrelatedTrackedPath, StringComparison.Ordinal));

            var status = StatusPorcelain(root);
            Assert.Contains($" M {UnrelatedTrackedPath}", status);
            Assert.Contains("?? untracked.txt", status);
            Assert.DoesNotContain(SeedPath, status);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Execute_commits_renamed_paths_together()
    {
        var root = CreateRepo();
        try
        {
            File.Delete(Path.Combine(root, SeedPath));
            Write(root, "renamed.txt", "same content moved by the run\n");
            var headBefore = HeadOid(root);

            var preview = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath, "renamed.txt" },
                Array.Empty<string>(),
                CancellationToken.None);
            var outcome = await Service().ExecuteApprovedAsync(preview.Plan!, "rename commit", CancellationToken.None);

            Assert.Equal(ManagedCommitPhase.Committed, outcome.Phase);
            Assert.Equal(headBefore, ParentOid(root));
            Assert.Contains(
                DiffNameStatus(root, "HEAD^", "HEAD"),
                line => line.EndsWith("renamed.txt", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Execute_aborts_when_head_moved_after_preview()
    {
        var root = CreateRepo();
        try
        {
            Write(root, SeedPath, "attributed change\n");
            var preview = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);

            Write(root, "concurrent.txt", "concurrent work\n");
            RunGit(root, "add", "concurrent.txt");
            RunGit(root, "commit", "-m", "concurrent");
            var concurrentHead = HeadOid(root);

            var outcome = await Service().ExecuteApprovedAsync(preview.Plan!, "stale commit", CancellationToken.None);

            Assert.Equal(ManagedCommitPhase.Aborted, outcome.Phase);
            Assert.Equal("head_moved", outcome.Code);
            Assert.Equal(concurrentHead, HeadOid(root));
            Assert.Empty(StagedNameStatus(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Execute_aborts_when_the_real_index_gains_changes_after_preview()
    {
        var root = CreateRepo();
        try
        {
            Write(root, SeedPath, "attributed change\n");
            var preview = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);

            Write(root, UnrelatedTrackedPath, "concurrently staged by someone else\n");
            RunGit(root, "add", UnrelatedTrackedPath);
            var mutatedHead = HeadOid(root);

            var outcome = await Service().ExecuteApprovedAsync(preview.Plan!, "blocked commit", CancellationToken.None);

            Assert.Equal(ManagedCommitPhase.Aborted, outcome.Phase);
            Assert.Equal("index_not_empty", outcome.Code);
            Assert.Equal(mutatedHead, HeadOid(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Execute_aborts_when_an_approved_path_changed_after_preview()
    {
        var root = CreateRepo();
        try
        {
            Write(root, SeedPath, "attributed change\n");
            var preview = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);

            Write(root, SeedPath, "changed again after approval\n");

            var outcome = await Service().ExecuteApprovedAsync(preview.Plan!, "stale worktree", CancellationToken.None);

            Assert.Equal(ManagedCommitPhase.Aborted, outcome.Phase);
            Assert.Equal("worktree_changed", outcome.Code);
            Assert.DoesNotContain(
                StagedNameStatus(root),
                line => line.EndsWith(SeedPath, StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Hook_failure_restores_pre_operation_index_bytes_when_state_is_service_owned()
    {
        var root = CreateRepoWithHook("#!/bin/sh\nexit 1\n");
        try
        {
            Write(root, SeedPath, "attributed change\n");
            var indexBefore = IndexFileBytes(root);
            var preview = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);

            var outcome = await Service().ExecuteApprovedAsync(preview.Plan!, "hook fails", CancellationToken.None);

            Assert.Equal(ManagedCommitPhase.Failed, outcome.Phase);
            Assert.Equal("commit_failed", outcome.Code);
            Assert.Equal(indexBefore, IndexFileBytes(root));
            Assert.True(RealIndexHasNoStagedChanges(root));
            Write(root, SeedPath, "still dirty after failed attempt\n");
            Assert.Contains(SeedPath, StatusPorcelain(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Hook_that_mutates_the_index_routes_to_reconciliation_instead_of_restoring()
    {
        var root = CreateRepoWithHook(
            "#!/bin/sh\nprintf 'injected\\n' > injected.txt\ngit add injected.txt\nexit 1\n");
        try
        {
            Write(root, SeedPath, "attributed change\n");
            var preview = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);

            var outcome = await Service().ExecuteApprovedAsync(preview.Plan!, "mutating hook", CancellationToken.None);

            Assert.Equal(ManagedCommitPhase.ReconciliationRequired, outcome.Phase);
            Assert.Null(outcome.NewHeadOid);
            Assert.Contains(
                StagedNameStatus(root),
                line => line.EndsWith("injected.txt", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Hook_mutating_the_worktree_flags_reconciliation_even_when_the_commit_succeeds()
    {
        var root = CreateRepoWithHook(
            "#!/bin/sh\nprintf 'hook touched this\\n' > other.txt\nexit 0\n");
        try
        {
            Write(root, SeedPath, "attributed change\n");
            Write(root, UnrelatedTrackedPath, "unrelated baseline\n");
            var preview = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);

            var outcome = await Service().ExecuteApprovedAsync(preview.Plan!, "sneaky hook", CancellationToken.None);

            Assert.Equal(ManagedCommitPhase.ReconciliationRequired, outcome.Phase);
            Assert.NotNull(outcome.NewHeadOid);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Overlong_or_invalid_messages_are_rejected_before_any_git_mutation()
    {
        var root = CreateRepo();
        try
        {
            Write(root, SeedPath, "attributed change\n");
            var preview = await Service().BuildPreviewAsync(
                root,
                new[] { SeedPath },
                Array.Empty<string>(),
                CancellationToken.None);
            var indexBefore = IndexFileBytes(root);

            var overlong = await Service().ExecuteApprovedAsync(
                preview.Plan!, new string('m', ManagedCommitService.MaxCommitMessageChars + 1), CancellationToken.None);
            var controlChar = await Service().ExecuteApprovedAsync(preview.Plan!, "bad\u0000message", CancellationToken.None);

            Assert.Equal(ManagedCommitPhase.Aborted, overlong.Phase);
            Assert.Equal("invalid_message", overlong.Code);
            Assert.Equal(ManagedCommitPhase.Aborted, controlChar.Phase);
            Assert.Equal("invalid_message", controlChar.Code);
            Assert.Equal(indexBefore, IndexFileBytes(root));
        }
        finally
        {
            TryDelete(root);
        }
    }

    // ------------------------------------------------------------------
    // Fixtures.
    // ------------------------------------------------------------------

    private static ManagedCommitService Service() => new(Cli);

    private static string CreateRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-git-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "ru@example.test");
        RunGit(root, "config", "user.name", "Ru Tests");
        RunGit(root, "config", "commit.gpgsign", "false");
        RunGit(root, "config", "core.autocrlf", "false");
        Write(root, SeedPath, "seed\n");
        Write(root, UnrelatedTrackedPath, "unrelated baseline\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "seed");
        return root;
    }

    private static string CreateRepoWithHook(string hookScript)
    {
        var root = CreateRepo();
        var hooksDirectory = Path.Combine(GitDir(root), "hooks");
        Directory.CreateDirectory(hooksDirectory);
        File.WriteAllText(Path.Combine(hooksDirectory, "pre-commit"), hookScript);
        return root;
    }

    private static void Write(string root, string relativePath, string content) =>
        File.WriteAllText(Path.Combine(root, relativePath), content);

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        if (!process.WaitForExit(30_000))
        {
            throw new InvalidOperationException("git fixture command timed out");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git fixture command failed ({process.ExitCode}): git {string.Join(' ', arguments)} :: {process.StandardError.ReadToEnd()}");
        }
    }

    private static string RunGitCapture(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        if (!process.WaitForExit(30_000))
        {
            throw new InvalidOperationException("git fixture command timed out");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git fixture command failed ({process.ExitCode}): git {string.Join(' ', arguments)} :: {process.StandardError.ReadToEnd()}");
        }

        return process.StandardOutput.ReadToEnd();
    }

    private static string GitDir(string root) =>
        RunGitCapture(root, "rev-parse", "--absolute-git-dir").Trim().Replace('/', Path.DirectorySeparatorChar);

    private static byte[] IndexFileBytes(string root) =>
        File.ReadAllBytes(Path.Combine(GitDir(root), "index"));

    private static string HeadOid(string root) => RunGitCapture(root, "rev-parse", "HEAD").Trim();

    private static string ParentOid(string root) => RunGitCapture(root, "rev-parse", "HEAD^").Trim();

    private static string StatusPorcelain(string root) => RunGitCapture(root, "status", "--porcelain");

    private static string[] DiffNameStatus(string root, string from, string to) =>
        RunGitCapture(root, "diff", "--name-status", from, to)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] StagedNameStatus(string root) =>
        RunGitCapture(root, "diff", "--cached", "--name-status")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool RealIndexHasNoStagedChanges(string root)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add("--cached");
        startInfo.ArgumentList.Add("--quiet");
        using var process = Process.Start(startInfo)!;
        process.WaitForExit(30_000);
        return process.ExitCode == 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
