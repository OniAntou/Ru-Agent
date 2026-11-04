using RuBackend.Harness;
using RuBackend.Harness.Workspace;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class HarnessSessionDeletionCoordinatorTests
{
    private static ScratchWorkspaceStore NewScratch(string root) => new(root);

    [Fact]
    public async Task A_scratch_workspace_is_deleted_with_its_session()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-scratch-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scratch = NewScratch(root);
            const string sessionId = "11111111111111111111111111111111";
            var path = scratch.Ensure(sessionId);
            var trace = new List<string>();
            var coordinator = new HarnessSessionDeletionCoordinator(
                new RecordingArtifactStore(trace),
                new RecordingSessionStore(trace, deleteResult: true),
                TimeProvider.System,
                scratchWorkspaces: scratch);

            var outcome = await coordinator.DeleteAsync(sessionId, CancellationToken.None);

            Assert.Equal(HarnessSessionDeletionOutcome.Deleted, outcome);
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Failed_primary_delete_preserves_the_session_scratch_workspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-scratch-primary-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scratch = NewScratch(root);
            const string sessionId = "66666666666666666666666666666666";
            var path = scratch.Ensure(sessionId);
            var file = Path.Combine(path, "work.txt");
            File.WriteAllText(file, "session data");
            var coordinator = new HarnessSessionDeletionCoordinator(
                new RecordingArtifactStore([]),
                new RecordingSessionStore([], deleteResult: false),
                TimeProvider.System,
                scratchWorkspaces: scratch);

            var outcome = await coordinator.DeleteAsync(sessionId, CancellationToken.None);

            Assert.Equal(HarnessSessionDeletionOutcome.SessionDeleteFailed, outcome);
            Assert.True(Directory.Exists(path));
            Assert.Equal("session data", await File.ReadAllTextAsync(file));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_retry_after_the_session_row_is_gone_still_deletes_its_scratch_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-scratch-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scratch = NewScratch(root);
            const string sessionId = "22222222222222222222222222222222";
            var path = scratch.Ensure(sessionId);
            var locked = Path.Combine(path, "locked.bin");
            File.WriteAllText(locked, "x");

            var trace = new List<string>();
            var sessions = new RecordingSessionStore(trace, deleteResult: false) { PrimaryMissing = true };
            var coordinator = new HarnessSessionDeletionCoordinator(
                new RecordingArtifactStore(trace),
                sessions,
                TimeProvider.System,
                scratchWorkspaces: scratch);

            string outcome;
            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                outcome = (await coordinator.DeleteAsync(sessionId, CancellationToken.None)).ToString();
                Assert.Equal(nameof(HarnessSessionDeletionOutcome.ArtifactsPending), outcome);
                Assert.True(Directory.Exists(path));
            }

            outcome = (await coordinator.DeleteAsync(sessionId, CancellationToken.None)).ToString();
            Assert.Equal(nameof(HarnessSessionDeletionOutcome.Deleted), outcome);
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task A_folder_ru_did_not_create_is_never_deleted()
    {
        var root = Path.Combine(Path.GetTempPath(), "ru-scratch-foreign-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var scratch = NewScratch(root);
            const string sessionId = "33333333333333333333333333333333";
            var path = Path.Combine(scratch.Root, sessionId);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "user.txt"), "keep");

            var trace = new List<string>();
            var coordinator = new HarnessSessionDeletionCoordinator(
                new RecordingArtifactStore(trace),
                new RecordingSessionStore(trace, deleteResult: true),
                TimeProvider.System,
                scratchWorkspaces: scratch);

            var outcome = await coordinator.DeleteAsync(sessionId, CancellationToken.None);

            Assert.Equal(HarnessSessionDeletionOutcome.Deleted, outcome);
            Assert.True(File.Exists(Path.Combine(path, "user.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
