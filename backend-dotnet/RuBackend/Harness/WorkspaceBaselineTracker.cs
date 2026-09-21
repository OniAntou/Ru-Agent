using System.Collections.Concurrent;

namespace RuBackend.Harness;

/// <summary>
/// Tracks which runs already hold a workspace baseline for the drive session in progress.
/// </summary>
/// <remarks>
/// Capturing a baseline reads and hashes every file in the workspace, up to the monitor's
/// 4096-file cap. Measured against this repository that is 645 MB hashed and 204 MB of
/// retained content in about 22 seconds. The drive passes are re-entered on every poll tick
/// while a run is settling, so capturing per pass re-read the workspace thousands of times
/// per run; only the first pass of a drive session needs it, because the watcher is live from
/// that moment and a detected change re-registers its own path.
///
/// The release is not optional bookkeeping: a baseline belongs to the watch that was started
/// with it. When a pass stops driving, its baseline goes too, so the next session captures a
/// fresh one instead of trusting hashes nothing is watching any more.
/// </remarks>
internal sealed class WorkspaceBaselineTracker
{
    private readonly ConcurrentDictionary<string, byte> _captured = new(StringComparer.Ordinal);

    /// <summary>
    /// True for the first call of a drive session, false for every later pass of the same one.
    /// </summary>
    internal bool ShouldCapture(string runId) =>
        _captured.TryAdd(runId, 0);

    /// <summary>Ends the drive session, so the next one captures again.</summary>
    internal void Release(string runId) =>
        _captured.TryRemove(runId, out _);
}
