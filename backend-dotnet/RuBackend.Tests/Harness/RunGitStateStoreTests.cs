using RuAgent.Protocol.Harness;
using RuBackend.Harness.Git;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class RunGitStateStoreTests
{
    private const string RunId = "run_" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task Start_snapshot_round_trips_across_restart()
    {
        var dbPath = NewDbPath();
        var original = Snapshot(eligible: true);
        try
        {
            using var first = new SqliteRunGitStateStore(dbPath);
            await first.SaveStartSnapshotAsync(RunId, original);

            using var reopened = new SqliteRunGitStateStore(dbPath);
            var restored = await reopened.TryGetStartSnapshotAsync(RunId);

            Assert.NotNull(restored);
            AssertSnapshotEqual(original, restored!);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Unknown_run_returns_null_start_snapshot()
    {
        var dbPath = NewDbPath();
        try
        {
            using var store = new SqliteRunGitStateStore(dbPath);

            Assert.Null(await store.TryGetStartSnapshotAsync("run_" + new string('9', 32)));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Attributed_paths_round_trip_across_restart()
    {
        var dbPath = NewDbPath();
        try
        {
            using var first = new SqliteRunGitStateStore(dbPath);
            await first.SaveStartSnapshotAsync(RunId, Snapshot(eligible: true));
            await first.SetAttributedPathsAsync(RunId, new[] { "src/a.txt", "src/b with space.txt" });

            using var reopened = new SqliteRunGitStateStore(dbPath);
            var restored = await reopened.TryGetAttributedPathsAsync(RunId);

            Assert.NotNull(restored);
            Assert.Equal(new[] { "src/a.txt", "src/b with space.txt" }, restored);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Refreshed_lease_rehydration_returns_snapshot_and_attributed_paths_together()
    {
        var dbPath = NewDbPath();
        try
        {
            var original = Snapshot(eligible: false);
            using var first = new SqliteRunGitStateStore(dbPath);
            await first.SaveStartSnapshotAsync(RunId, original);
            await first.SetAttributedPathsAsync(RunId, new[] { "kept.txt" });

            // A Broker restart opens a fresh store instance over the same Host database and
            // rehydrates the bounded start state it needs for the refreshed lease.
            using var reopened = new SqliteRunGitStateStore(dbPath);
            var rehydrated = await reopened.TryLoadForRefreshedLeaseAsync(RunId);

            Assert.NotNull(rehydrated);
            AssertSnapshotEqual(original, rehydrated!.StartSnapshot);
            Assert.Equal(new[] { "kept.txt" }, rehydrated.AttributedPaths);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Ineligible_commit_flag_is_permanent_for_the_run()
    {
        var dbPath = NewDbPath();
        try
        {
            using var store = new SqliteRunGitStateStore(dbPath);
            await store.SaveStartSnapshotAsync(RunId, Snapshot(eligible: false));

            var laterAttempt = Snapshot(eligible: true);
            await store.SaveStartSnapshotAsync(RunId, laterAttempt);

            var restored = await store.TryGetStartSnapshotAsync(RunId);
            Assert.NotNull(restored);
            Assert.False(restored!.ManagedCommitEligible);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Eligible_flag_can_never_flip_after_ineligibility_was_recorded()
    {
        var dbPath = NewDbPath();
        try
        {
            using var first = new SqliteRunGitStateStore(dbPath);
            await first.SaveStartSnapshotAsync(RunId, Snapshot(eligible: false));

            using var reopened = new SqliteRunGitStateStore(dbPath);
            await reopened.SaveStartSnapshotAsync(RunId, Snapshot(eligible: true));

            Assert.False((await reopened.TryGetStartSnapshotAsync(RunId))!.ManagedCommitEligible);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Raw_file_content_is_never_persisted_as_plaintext()
    {
        var dbPath = NewDbPath();
        const string secretContent = "RU-PRIVATE-CONTENT-MARKER-do-not-store-9e37";
        try
        {
            var snapshot = Snapshot(
                eligible: true,
                configure: hashes => hashes["leaked.txt"] = Sha256Hex(secretContent));

            using (var store = new SqliteRunGitStateStore(dbPath))
            {
                await store.SaveStartSnapshotAsync(RunId, snapshot);

                Assert.Equal(Sha256Hex(secretContent), (await store.TryGetStartSnapshotAsync(RunId))!.ObservedFileHashes["leaked.txt"]);
            }

            var rawBytes = await File.ReadAllBytesAsync(dbPath);
            var rawText = System.Text.Encoding.Latin1.GetString(rawBytes);
            Assert.DoesNotContain(secretContent, rawText, StringComparison.Ordinal);
            Assert.Contains("leaked.txt", rawText, StringComparison.Ordinal);
            Assert.Contains(Sha256Hex(secretContent), rawText, StringComparison.Ordinal);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Oversized_entry_list_is_bounded_on_save()
    {
        var dbPath = NewDbPath();
        try
        {
            var entries = Enumerable.Range(0, SqliteRunGitStateStore.MaxStoredEntries + 100)
                .Select(i => new RunGitStatusEntry(".", "?", RunGitEntryKind.Untracked, false, $"u{i}.txt", null, null))
                .ToList();
            var oversized = Snapshot(eligible: true, entries: entries);

            using var store = new SqliteRunGitStateStore(dbPath);
            await store.SaveStartSnapshotAsync(RunId, oversized);

            var restored = await store.TryGetStartSnapshotAsync(RunId);
            Assert.NotNull(restored);
            Assert.Equal(SqliteRunGitStateStore.MaxStoredEntries, restored!.Entries.Count);
            Assert.True(restored.StatusTruncated);
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Fact]
    public async Task Attributed_paths_reject_unbounded_input_before_persistence()
    {
        var dbPath = NewDbPath();
        try
        {
            using var store = new SqliteRunGitStateStore(dbPath);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                store.SetAttributedPathsAsync(
                    RunId,
                    Enumerable.Repeat("src/file.txt", SqliteRunGitStateStore.MaxStoredEntries + 1).ToArray()));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    private static string NewDbPath() =>
        Path.Combine(Path.GetTempPath(), "ru-git-state-" + Guid.NewGuid().ToString("N") + ".db");

    private static RunGitWorktreeSnapshot Snapshot(
        bool eligible,
        List<RunGitStatusEntry>? entries = null,
        Action<Dictionary<string, string>>? configure = null)
    {
        var hashes = new Dictionary<string, string>
        {
            ["seed.txt"] = new string('a', 64),
        };
        configure?.Invoke(hashes);
        return new RunGitWorktreeSnapshot(
            RepositoryRoot: @"C:\tmp\repo",
            HeadOid: new string('1', 40),
            BranchName: "main",
            IsDetached: false,
            IndexHash: new string('2', 64),
            HasStagedChanges: !eligible,
            ManagedCommitEligible: eligible,
            Entries: entries ?? new List<RunGitStatusEntry>
            {
                new(".", "M", RunGitEntryKind.Tracked, false, "seed.txt", null, null),
            },
            ObservedFileHashes: hashes,
            StatusTruncated: false,
            FileHashesTruncated: false,
            CapturedAtUtc: new DateTimeOffset(2026, 8, 26, 8, 0, 0, TimeSpan.Zero));
    }

    private static void AssertSnapshotEqual(RunGitWorktreeSnapshot expected, RunGitWorktreeSnapshot actual)
    {
        Assert.Equal(expected.RepositoryRoot, actual.RepositoryRoot);
        Assert.Equal(expected.HeadOid, actual.HeadOid);
        Assert.Equal(expected.BranchName, actual.BranchName);
        Assert.Equal(expected.IsDetached, actual.IsDetached);
        Assert.Equal(expected.IndexHash, actual.IndexHash);
        Assert.Equal(expected.HasStagedChanges, actual.HasStagedChanges);
        Assert.Equal(expected.ManagedCommitEligible, actual.ManagedCommitEligible);
        Assert.Equal(expected.StatusTruncated, actual.StatusTruncated);
        Assert.Equal(expected.FileHashesTruncated, actual.FileHashesTruncated);
        Assert.Equal(expected.CapturedAtUtc, actual.CapturedAtUtc);
        Assert.Equal(expected.Entries.Count, actual.Entries.Count);
        for (var i = 0; i < expected.Entries.Count; i++)
        {
            Assert.Equal(expected.Entries[i], actual.Entries[i]);
        }

        Assert.Equal(expected.ObservedFileHashes.Count, actual.ObservedFileHashes.Count);
        foreach (var (path, hash) in expected.ObservedFileHashes)
        {
            Assert.True(actual.ObservedFileHashes.TryGetValue(path, out var actualHash), path);
            Assert.Equal(hash, actualHash);
        }
    }

    private static string Sha256Hex(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
        }
    }
}
