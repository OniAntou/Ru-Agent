using System.Diagnostics;
using RuAgent.Broker.Tools.Git;
using RuAgent.Protocol.Harness;
using Xunit;

namespace RuAgent.Broker.Tests;

/// <summary>
/// These tests spawn real git processes against temporary repositories; disabling
/// parallelization keeps their timing isolated from the rest of the suite.
/// </summary>
[CollectionDefinition(nameof(GitProcessHeavyCollection), DisableParallelization = true)]
public sealed class GitProcessHeavyCollection;

[Collection(nameof(GitProcessHeavyCollection))]
public sealed class GitChangeAttributorTests
{
    private static readonly IGitCli Cli = new StreamingGitCli();

    [Fact]
    public async Task Clean_repo_start_snapshot_captures_head_branch_index_hashes()
    {
        var root = CreateRepo();
        try
        {
            var snapshot = await CaptureAsync(root);

            Assert.Equal(root, snapshot.RepositoryRoot);
            Assert.Matches("^[0-9a-f]{40}$", snapshot.HeadOid!);
            Assert.False(snapshot.IsDetached);
            Assert.False(string.IsNullOrWhiteSpace(snapshot.BranchName));
            Assert.False(snapshot.HasStagedChanges);
            Assert.True(snapshot.ManagedCommitEligible);
            Assert.Matches("^[0-9a-f]{64}$", snapshot.IndexHash);

            var again = await CaptureAsync(root);
            Assert.Equal(snapshot.IndexHash, again.IndexHash);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Dirty_file_at_start_is_excluded_not_attributed()
    {
        var root = CreateRepo();
        try
        {
            File.WriteAllText(Path.Combine(root, "seed.txt"), "dirty before run\n");
            var start = await CaptureAsync(root);
            var end = await CaptureAsync(root);

            var result = new GitChangeAttributor().Attribute(start, end, Array.Empty<RunGitMutationEvidence>());

            Assert.Empty(result.AttributedPaths);
            Assert.Empty(result.AmbiguousPaths);
            Assert.Contains("seed.txt", result.ExcludedPreExistingPaths);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Verified_file_tool_mutation_delta_is_attributed()
    {
        var root = CreateRepo();
        try
        {
            var start = await CaptureAsync(root);
            File.WriteAllText(Path.Combine(root, "seed.txt"), "mutated by file tool\n");
            var end = await CaptureAsync(root);

            var evidence = EvidenceFor(start, end, "seed.txt");
            var result = new GitChangeAttributor().Attribute(start, end, evidence);

            Assert.Contains("seed.txt", result.AttributedPaths);
            Assert.DoesNotContain(result.AmbiguousPaths, p => p == "seed.txt");
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Change_without_evidence_is_flagged_ambiguous_never_attributed()
    {
        var root = CreateRepo();
        try
        {
            var start = await CaptureAsync(root);
            File.WriteAllText(Path.Combine(root, "seed.txt"), "changed by unknown actor\n");
            var end = await CaptureAsync(root);

            var result = new GitChangeAttributor().Attribute(start, end, Array.Empty<RunGitMutationEvidence>());

            Assert.Empty(result.AttributedPaths);
            Assert.Contains("seed.txt", result.AmbiguousPaths);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Pre_existing_dirty_path_changed_again_without_evidence_stays_ambiguous()
    {
        var root = CreateRepo();
        try
        {
            File.WriteAllText(Path.Combine(root, "seed.txt"), "pre-existing dirty\n");
            var start = await CaptureAsync(root);
            File.WriteAllText(Path.Combine(root, "seed.txt"), "changed again during run\n");
            var end = await CaptureAsync(root);

            var result = new GitChangeAttributor().Attribute(start, end, Array.Empty<RunGitMutationEvidence>());

            Assert.Empty(result.AttributedPaths);
            Assert.Contains("seed.txt", result.AmbiguousPaths);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task New_file_created_during_run_without_evidence_is_ambiguous()
    {
        var root = CreateRepo();
        try
        {
            var start = await CaptureAsync(root);
            File.WriteAllText(Path.Combine(root, "created.txt"), "created during run\n");
            var end = await CaptureAsync(root);

            var result = new GitChangeAttributor().Attribute(start, end, Array.Empty<RunGitMutationEvidence>());

            Assert.Empty(result.AttributedPaths);
            Assert.Contains("created.txt", result.AmbiguousPaths);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Verified_terminal_window_delta_is_attributed()
    {
        var root = CreateRepo();
        try
        {
            var start = await CaptureAsync(root);
            File.WriteAllText(Path.Combine(root, "seed.txt"), "rewritten inside terminal window\n");
            var end = await CaptureAsync(root);

            var evidence = EvidenceFor(start, end, "seed.txt");
            var result = new GitChangeAttributor().Attribute(start, end, evidence);

            Assert.Contains("seed.txt", result.AttributedPaths);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Stale_evidence_hash_mismatch_keeps_path_ambiguous()
    {
        var root = CreateRepo();
        try
        {
            var start = await CaptureAsync(root);
            File.WriteAllText(Path.Combine(root, "seed.txt"), "changed for stale evidence\n");
            var end = await CaptureAsync(root);

            var evidence = new[]
            {
                new RunGitMutationEvidence(
                    "seed.txt",
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"),
            };
            var result = new GitChangeAttributor().Attribute(start, end, evidence);

            Assert.Empty(result.AttributedPaths);
            Assert.Contains("seed.txt", result.AmbiguousPaths);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Deleted_file_with_missing_after_hash_is_attributed()
    {
        var root = CreateRepo();
        try
        {
            File.WriteAllText(Path.Combine(root, "seed.txt"), "modified before deletion\n");
            var start = await CaptureAsync(root);
            Assert.Contains("seed.txt", start.ObservedFileHashes.Keys);

            File.Delete(Path.Combine(root, "seed.txt"));
            var end = await CaptureAsync(root);

            Assert.Equal("missing", end.ObservedFileHashes["seed.txt"]);

            var evidence = new[]
            {
                new RunGitMutationEvidence("seed.txt", start.ObservedFileHashes["seed.txt"], "missing"),
            };
            var result = new GitChangeAttributor().Attribute(start, end, evidence);

            Assert.Contains("seed.txt", result.AttributedPaths);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Non_empty_index_at_start_disables_managed_commit_eligibility()
    {
        var root = CreateRepo();
        try
        {
            File.WriteAllText(Path.Combine(root, "staged.txt"), "staged before run\n");
            RunGit(root, "add", ".");

            var start = await CaptureAsync(root);

            Assert.True(start.HasStagedChanges);
            Assert.False(start.ManagedCommitEligible);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Locator_resolves_repository_root_from_nested_directory()
    {
        var root = CreateRepo();
        try
        {
            var nested = Path.Combine(root, "deep", "deeper");
            Directory.CreateDirectory(nested);

            var location = await new GitRepositoryLocator(Cli).LocateAsync(nested, CancellationToken.None);

            Assert.Equal(GitRepositoryResolution.Found, location.Resolution);
            Assert.Equal(root, location.RepositoryRoot);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task Directory_outside_any_repository_reports_no_repository()
    {
        var plain = Path.Combine(Path.GetTempPath(), "ru-git-none-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(plain);
        try
        {
            var location = await new GitRepositoryLocator(Cli).LocateAsync(plain, CancellationToken.None);

            Assert.Equal(GitRepositoryResolution.NoRepository, location.Resolution);
            Assert.Null(location.RepositoryRoot);
        }
        finally
        {
            TryDelete(plain);
        }
    }

    [Fact]
    public async Task Failed_git_invocation_never_produces_a_clean_snapshot()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ru-git-missing-" + Guid.NewGuid().ToString("N"));

        var location = await new GitRepositoryLocator(Cli).LocateAsync(missing, CancellationToken.None);
        Assert.Equal(GitRepositoryResolution.Failed, location.Resolution);

        var snapshot = await GitWorktreeSnapshot.CaptureAsync(missing, Cli, CancellationToken.None);
        Assert.Null(snapshot);
    }

    private static async Task<RunGitWorktreeSnapshot> CaptureAsync(string root)
    {
        var snapshot = await GitWorktreeSnapshot.CaptureAsync(root, Cli, CancellationToken.None);
        Assert.NotNull(snapshot);
        return snapshot!;
    }

    private static IReadOnlyList<RunGitMutationEvidence> EvidenceFor(
        RunGitWorktreeSnapshot start,
        RunGitWorktreeSnapshot end,
        string path) =>
        new[]
        {
            new RunGitMutationEvidence(
                path,
                start.ObservedFileHashes.TryGetValue(path, out var before) ? before : GitChangeAttributor.AbsentHash,
                end.ObservedFileHashes.TryGetValue(path, out var after) ? after : GitChangeAttributor.AbsentHash),
        };

    private static string CreateRepo()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-git-attr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        RunGit(root, "init");
        RunGit(root, "config", "user.email", "ru@example.test");
        RunGit(root, "config", "user.name", "Ru Tests");
        RunGit(root, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(root, "seed.txt"), "seed\n");
        RunGit(root, "add", ".");
        RunGit(root, "commit", "-m", "seed");
        return root;
    }

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
