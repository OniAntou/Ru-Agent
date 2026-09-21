using RuBackend.Harness;
using Xunit;

namespace RuBackend.Tests.Harness;

/// <summary>
/// Capturing a workspace baseline is once per drive session, not once per drive pass.
/// </summary>
/// <remarks>
/// The drive passes are re-entered on every poll tick while a run settles, and a capture
/// reads and hashes the whole workspace -- measured at 645 MB hashed and 204 MB retained in
/// about 22 seconds against this repository at the monitor's 4096-file cap. Capturing per
/// pass was the host's most expensive steady-state work; the tracker is what keeps it to the
/// first pass, so its two answers are worth pinning.
/// </remarks>
public sealed class WorkspaceBaselineTrackerTests
{
    [Fact]
    public void The_first_pass_of_a_drive_session_captures()
    {
        var tracker = new WorkspaceBaselineTracker();

        Assert.True(tracker.ShouldCapture("run-a"));
    }

    [Fact]
    public void Later_passes_of_the_same_session_do_not_capture_again()
    {
        var tracker = new WorkspaceBaselineTracker();

        Assert.True(tracker.ShouldCapture("run-a"));
        Assert.False(tracker.ShouldCapture("run-a"));
        Assert.False(tracker.ShouldCapture("run-a"));
    }

    [Fact]
    public void Runs_are_tracked_independently()
    {
        var tracker = new WorkspaceBaselineTracker();

        Assert.True(tracker.ShouldCapture("run-a"));
        Assert.True(tracker.ShouldCapture("run-b"));
        Assert.False(tracker.ShouldCapture("run-a"));
    }

    [Fact]
    public void Ending_a_session_makes_the_next_one_capture_afresh()
    {
        // The baseline belongs to the watch started with it. A later session must not inherit
        // hashes nothing is watching, or an external change between them goes unreported.
        var tracker = new WorkspaceBaselineTracker();

        Assert.True(tracker.ShouldCapture("run-a"));
        tracker.Release("run-a");
        Assert.True(tracker.ShouldCapture("run-a"));
    }

    [Fact]
    public void Releasing_a_run_that_never_captured_is_harmless()
    {
        // The drive's finally runs on paths that returned before the capture, so this happens.
        var tracker = new WorkspaceBaselineTracker();

        tracker.Release("never-seen");

        Assert.True(tracker.ShouldCapture("never-seen"));
    }
}
