using RuBackend.Harness;
using RuAgent.Harness.Persistence;
using Xunit;

namespace RuBackend.Tests.Harness;

public sealed partial class HarnessSessionDeletionCoordinatorTests
{
    [Theory]
    [InlineData(0, HarnessSessionDeletionOutcome.SessionNotFound)]
    [InlineData(2, HarnessSessionDeletionOutcome.Deleted)]
    public async Task Confirmed_missing_primary_reports_whether_retained_harness_cleanup_completed(int removedRuns,
        HarnessSessionDeletionOutcome expected)
    {
        var trace = new List<string>();
        var coordinator = new HarnessSessionDeletionCoordinator(new RecordingArtifactStore(trace)
            { DeleteResult = new HarnessSessionArtifactDeleteResult(0, removedRuns) },
            new RecordingSessionStore(trace, false) { PrimaryMissing = true }, TimeProvider.System);
        Assert.Equal(expected, await coordinator.DeleteAsync(new string('a', 32), default));
    }

    [Fact]
    public async Task Primary_store_failure_cannot_report_success_from_removed_harness_rows()
    {
        var trace = new List<string>();
        var coordinator = new HarnessSessionDeletionCoordinator(new RecordingArtifactStore(trace),
            new RecordingSessionStore(trace, false), TimeProvider.System);
        var outcome = await coordinator.DeleteAsync(new string('a', 32), default);
        Assert.Equal(HarnessSessionDeletionOutcome.SessionDeleteFailed, outcome);
        Assert.Equal(["tombstone", "harness", "session"], trace);
    }
}
