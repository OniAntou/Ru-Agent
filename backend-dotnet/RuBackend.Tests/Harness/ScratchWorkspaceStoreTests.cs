using System.Diagnostics;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed class ScratchWorkspaceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-scratch-workspace-" + Guid.NewGuid().ToString("N"));

    public ScratchWorkspaceStoreTests()
    {
        Directory.CreateDirectory(_root);
    }

    private ScratchWorkspaceStore Store => new(_root);

    private static string Session(char value) => new(value, 32);

    [Fact]
    public void Ensure_creates_a_marked_folder_under_the_data_root()
    {
        var store = Store;
        var sessionId = Session('a');

        var path = store.Ensure(sessionId);

        Assert.Equal(Path.Combine(store.Root, sessionId), path);
        Assert.True(Directory.Exists(path));
        var marker = File.ReadAllText(Path.Combine(path, ScratchWorkspaceStore.MarkerFileName));
        Assert.Contains(sessionId, marker, StringComparison.Ordinal);
        Assert.True(store.IsScratchPath(path));
        Assert.True(store.Exists(sessionId));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("../escaped")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public void Invalid_session_ids_are_rejected(string value)
    {
        var store = Store;

        Assert.False(ScratchWorkspaceStore.IsValidSessionId(value));
        Assert.Throws<ArgumentException>(() => store.PathFor(value));
        Assert.False(store.IsScratchPath(Path.Combine(store.Root, value)));
    }

    [Fact]
    public void IsScratchPath_only_accepts_direct_session_children()
    {
        var store = Store;
        var sessionId = Session('b');

        Assert.True(store.IsScratchPath(Path.Combine(store.Root, sessionId)));
        Assert.True(store.IsScratchPath(Path.Combine(store.Root, sessionId) + Path.DirectorySeparatorChar));
        Assert.False(store.IsScratchPath(Path.Combine(store.Root, sessionId, "nested")));
        Assert.False(store.IsScratchPath(Path.Combine(store.Root, "not-a-session")));
        Assert.False(store.IsScratchPath(Path.Combine(_root, "somewhere-else")));
        Assert.False(store.IsScratchPath(null));
    }

    [Fact]
    public async Task Concurrent_first_allocations_for_one_session_agree_on_the_owned_folder()
    {
        var store = Store;

        // The intake and run paths may ask for the same session's scratch folder at the same
        // time. A loser must wait for the winner's marker instead of refusing the unmarked
        // folder that the winner is still creating.
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            using var start = new Barrier(16);
            var allocations = Enumerable.Range(0, 16)
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        start.SignalAndWait();
                        return store.Ensure(sessionId);
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();

            var paths = await Task.WhenAll(allocations);

            Assert.Single(paths.Distinct(StringComparer.OrdinalIgnoreCase));
            Assert.True(store.Exists(sessionId));
            Assert.True(File.Exists(Path.Combine(paths[0], ScratchWorkspaceStore.MarkerFileName)));
        }
    }

    [Fact]
    public void TryDelete_removes_a_marked_folder_and_is_idempotent()
    {
        var store = Store;
        var sessionId = Session('c');
        var path = store.Ensure(sessionId);
        File.WriteAllText(Path.Combine(path, "notes.txt"), "scratch");

        Assert.True(store.TryDelete(sessionId));
        Assert.False(Directory.Exists(path));
        Assert.True(store.TryDelete(sessionId));
        Assert.False(store.Exists(sessionId));
    }

    [Fact]
    public void TryDelete_leaves_a_folder_ru_did_not_create()
    {
        var store = Store;
        var sessionId = Session('d');
        var path = Path.Combine(store.Root, sessionId);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "user.txt"), "keep me");

        Assert.True(store.TryDelete(sessionId));
        Assert.True(File.Exists(Path.Combine(path, "user.txt")));
    }

    [Fact]
    public void TryDelete_refuses_a_marker_for_another_session()
    {
        var store = Store;
        var sessionId = Session('e');
        var path = store.Ensure(sessionId);
        File.WriteAllText(
            Path.Combine(path, ScratchWorkspaceStore.MarkerFileName),
            "{\"Version\":1,\"SessionId\":\"" + Session('f') + "\"}");

        Assert.False(store.TryDelete(sessionId));
        Assert.True(Directory.Exists(path));
    }

    /// <summary>
    /// An agent's tools can rewrite the marker into one naming another session, for
    /// example by copying it from another folder. The folder is still named after this
    /// session and the Host's record proves the allocation, so allocation repairs it.
    /// </summary>
    [Fact]
    public void Ensure_repairs_a_marker_relabelled_to_another_session_when_the_record_proves_ownership()
    {
        var store = Store;
        var sessionId = Session('a');
        var path = store.Ensure(sessionId);
        File.WriteAllText(Path.Combine(path, "work.txt"), "keep");
        var marker = Path.Combine(path, ScratchWorkspaceStore.MarkerFileName);
        File.WriteAllText(marker, "{\"Version\":1,\"SessionId\":\"" + Session('f') + "\"}");

        Assert.Equal(path, store.Ensure(sessionId));

        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "work.txt")));
        Assert.Contains(sessionId, File.ReadAllText(marker), StringComparison.Ordinal);
        Assert.True(store.OwnsCanonicalWorkspace(path, sessionId));
    }

    [Fact]
    public void Ensure_leaves_a_matching_record_unwritten()
    {
        var store = Store;
        var sessionId = Session('6');
        store.Ensure(sessionId);
        var record = store.RecordPathFor(sessionId);
        var written = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(record, written);

        store.Ensure(sessionId);

        Assert.Equal(written, File.GetLastWriteTimeUtc(record));
    }

    [Fact]
    public async Task DeleteOrphansAsync_removes_dead_sessions_and_keeps_live_and_foreign_folders()
    {
        var store = Store;
        var live = Session('1');
        var dead = Session('2');
        var foreign = Session('3');
        store.Ensure(live);
        store.Ensure(dead);
        Directory.CreateDirectory(Path.Combine(store.Root, foreign));

        var deleted = await store.DeleteOrphansAsync(
            (id, _) => Task.FromResult(id == live),
            CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.True(Directory.Exists(Path.Combine(store.Root, live)));
        Assert.False(Directory.Exists(Path.Combine(store.Root, dead)));
        Assert.True(Directory.Exists(Path.Combine(store.Root, foreign)));
    }

    [Fact]
    public void A_locked_file_keeps_the_marker_so_a_retry_can_finish()
    {
        var store = Store;
        var sessionId = Session('5');
        var path = store.Ensure(sessionId);
        var lockedFile = Path.Combine(path, "locked.bin");
        File.WriteAllText(lockedFile, "x");
        var marker = Path.Combine(path, ScratchWorkspaceStore.MarkerFileName);

        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(store.TryDelete(sessionId));
            Assert.True(File.Exists(marker));
        }

        Assert.True(store.TryDelete(sessionId));
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void Ensure_is_idempotent()
    {
        var store = Store;
        var sessionId = Session('4');
        var first = store.Ensure(sessionId);
        File.WriteAllText(Path.Combine(first, "keep.txt"), "x");

        var second = store.Ensure(sessionId);

        Assert.Equal(first, second);
        Assert.True(File.Exists(Path.Combine(first, "keep.txt")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ensure_repairs_a_marker_the_agent_damaged(bool deletedMarker)
    {
        var store = Store;
        var sessionId = Session('b');
        var path = store.Ensure(sessionId);
        File.WriteAllText(Path.Combine(path, "work.txt"), "keep");
        var marker = Path.Combine(path, ScratchWorkspaceStore.MarkerFileName);
        if (deletedMarker)
        {
            File.Delete(marker);
        }
        else
        {
            File.WriteAllText(marker, "{\"note\":\"rewritten inside the workspace\"}");
        }

        var again = store.Ensure(sessionId);

        // The record the Host keeps outside the workspace still proves this exact
        // folder was allocated to the session, so its data survives and the
        // human-visible marker is repaired instead of the session dying.
        Assert.Equal(path, again);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(path, "work.txt")));
        Assert.Contains(sessionId, File.ReadAllText(marker), StringComparison.Ordinal);
        Assert.True(store.OwnsCanonicalWorkspace(path, sessionId));
    }

    [Fact]
    public void Ensure_still_refuses_a_damaged_marker_that_no_record_claims()
    {
        var store = Store;
        var sessionId = Session('c');
        var path = store.PathFor(sessionId);
        Directory.CreateDirectory(path);
        var marker = Path.Combine(path, ScratchWorkspaceStore.MarkerFileName);
        File.WriteAllText(marker, "{\"note\":\"placed by hand\"}");
        File.WriteAllText(Path.Combine(path, "keep.txt"), "user data");

        Assert.Throws<IOException>(() => store.Ensure(sessionId));
        Assert.Equal("{\"note\":\"placed by hand\"}", File.ReadAllText(marker));
        Assert.Equal("user data", File.ReadAllText(Path.Combine(path, "keep.txt")));
    }

    [Fact]
    public void TryDelete_removes_a_folder_whose_marker_was_damaged_and_drops_its_record()
    {
        var store = Store;
        var sessionId = Session('d');
        var path = store.Ensure(sessionId);
        var marker = Path.Combine(path, ScratchWorkspaceStore.MarkerFileName);
        File.Delete(marker);

        Assert.True(store.TryDelete(sessionId));

        Assert.False(Directory.Exists(path));
        Assert.False(File.Exists(store.RecordPathFor(sessionId)));
    }

    [Fact]
    public void The_record_never_sits_inside_the_workspace()
    {
        var store = Store;
        var sessionId = Session('e');

        var path = store.Ensure(sessionId);
        var record = store.RecordPathFor(sessionId);

        Assert.True(File.Exists(record));
        Assert.False(record.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ScratchWorkspaceStore.OwnersDirectoryName,
            Path.GetFileName(Path.GetDirectoryName(record)!));
    }

    [Fact]
    public void DeleteOrphans_drops_records_whose_folder_is_already_gone()
    {
        var store = Store;
        var live = Session('1');
        var dead = Session('2');
        store.Ensure(live);
        store.Ensure(dead);
        Directory.Delete(store.PathFor(dead), recursive: true);

        store.DeleteOrphans(id => id == live);

        Assert.False(File.Exists(store.RecordPathFor(dead)));
        Assert.True(File.Exists(store.RecordPathFor(live)));
        Assert.True(Directory.Exists(store.PathFor(live)));
    }

    [Fact]
    public void Ensure_refuses_to_claim_an_existing_unmarked_user_folder()
    {
        var store = Store;
        var sessionId = Session('7');
        var path = store.PathFor(sessionId);
        Directory.CreateDirectory(path);
        var userFile = Path.Combine(path, "keep.txt");
        File.WriteAllText(userFile, "user data");

        Assert.Throws<IOException>(() => store.Ensure(sessionId));
        Assert.Equal("user data", File.ReadAllText(userFile));
        Assert.False(File.Exists(Path.Combine(path, ScratchWorkspaceStore.MarkerFileName)));
    }

    [Fact]
    public void Ensure_refuses_to_overwrite_a_marker_for_another_session()
    {
        var store = Store;
        var sessionId = Session('8');
        var path = store.PathFor(sessionId);
        Directory.CreateDirectory(path);
        var userFile = Path.Combine(path, "keep.txt");
        var marker = Path.Combine(path, ScratchWorkspaceStore.MarkerFileName);
        File.WriteAllText(userFile, "user data");
        File.WriteAllText(marker, "{\"Version\":1,\"SessionId\":\"" + Session('9') + "\"}");

        Assert.Throws<IOException>(() => store.Ensure(sessionId));
        Assert.Equal("user data", File.ReadAllText(userFile));
        Assert.Contains(Session('9'), File.ReadAllText(marker), StringComparison.Ordinal);
    }

    [Fact]
    public void Ensure_refuses_a_reparse_point_workspace_path()
    {
        var store = Store;
        var sessionId = Session('a');
        var target = Path.Combine(_root, "outside-target");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(store.Root);
        var link = store.PathFor(sessionId);
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            Assert.NotNull(process);
            Assert.True(process.WaitForExit(15_000), "junction creation timed out");
            Assert.Equal(0, process.ExitCode);

            Assert.Throws<IOException>(() => store.Ensure(sessionId));
            Assert.False(File.Exists(Path.Combine(target, ScratchWorkspaceStore.MarkerFileName)));
            Assert.True(Directory.Exists(target));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link, recursive: false);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
