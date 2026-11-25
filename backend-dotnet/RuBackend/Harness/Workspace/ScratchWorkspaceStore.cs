using System.Text.Json;
using RuBackend.Business;

namespace RuBackend.Harness.Workspace;

/// <summary>
/// Ru-owned scratch workspaces for chats that never chose a project folder. They live under the
/// Ru data root, never inside the application directory: an install may be read-only, the folder
/// must not pollute the product's own repository, and uninstalling the app must not take user work
/// with it. A scratch workspace is an ordinary workspace for policy purposes; this store only owns
/// creation, identity and deletion.
/// </summary>
public sealed class ScratchWorkspaceStore
{
    public const string DirectoryName = "workspaces";
    public const string MarkerFileName = ".ru-scratch.json";

    /// <summary>
    /// Host-owned ownership records, one per scratch workspace, kept beside the
    /// workspaces instead of inside them: the marker is a file the agent's own
    /// tools can rewrite, so it cannot be the only witness of ownership.
    /// </summary>
    public const string OwnersDirectoryName = ".owners";
    private const int MarkerVersion = 1;
    private const int MaxMarkerBytes = 1024;
    private const int MaxRecordBytes = 1024;
    private const int MaxSweepDirectories = 10_000;
    private const int MaxDeleteDepth = 64;

    private readonly ILogger<ScratchWorkspaceStore>? _logger;

    // One allocation at a time: a second first-time allocation for the same session would
    // otherwise observe the winner's directory before its ownership marker is written and
    // refuse it as unowned. Allocation is rare and short, so serializing it is cheap.
    private readonly object _ensureGate = new();

    public ScratchWorkspaceStore(DataPaths paths, ILogger<ScratchWorkspaceStore>? logger = null)
        : this(paths.BusinessDataRoot, logger)
    {
    }

    public ScratchWorkspaceStore(string dataRoot, ILogger<ScratchWorkspaceStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        Root = Path.GetFullPath(Path.Combine(Path.GetFullPath(dataRoot), DirectoryName));
        _logger = logger;
    }

    /// <summary>The absolute directory that holds every scratch workspace.</summary>
    public string Root { get; }

    public static bool IsValidSessionId(string? value) =>
        value is { Length: 32 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// Creates the session's scratch folder and records its ownership. An existing folder is
    /// reused when either the marker or the Host's own record proves this session owns it, and
    /// whichever witness was lost is repaired: the marker lives where the agent's file tools
    /// can rewrite it, so a damaged marker must not strand the session. A folder neither
    /// witness claims is never adopted or relabelled.
    /// </summary>
    public string Ensure(string sessionId)
    {
        var path = PathFor(sessionId);
        lock (_ensureGate)
        {
            return EnsureCore(path, sessionId);
        }
    }

    private string EnsureCore(string path, string sessionId)
    {
        if (Directory.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                // Never write a marker through a junction: the target is not Ru's folder.
                throw new IOException("scratch_workspace_reparse");
            }

            var recorded = RecordMatches(path, sessionId);
            if (MarkerMatches(path, sessionId))
            {
                // Upgrade path: a workspace allocated before records existed, or a
                // data root that moved, gains its record on the next allocation. A
                // record that already matches is left alone, so a turn writes nothing.
                if (!recorded)
                {
                    TryWriteRecord(path, sessionId);
                }

                return path;
            }

            if (!recorded)
            {
                // Existing user data is not ours to claim. Without the Host's record,
                // never replace a missing, malformed or foreign ownership marker.
                throw new IOException("scratch_workspace_unowned");
            }

            // The agent's own tools can rewrite anything inside its workspace,
            // including the marker, even into one naming another session. The folder
            // is named after this session and the record proves Ru allocated it here,
            // so no other session can own it: repair the marker and keep the data.
            // Deletion stays stricter (TryDelete) because it cannot be undone.
            if (MarkerNamesAnotherSession(path, sessionId))
            {
                _logger?.LogWarning("scratch workspace {SessionId} marker named another session; repairing it from the Host record", sessionId);
            }

            TryWriteMarker(path, sessionId);
            return path;
        }

        Directory.CreateDirectory(path);
        var markerWritten = TryWriteMarker(path, sessionId);
        var recordWritten = TryWriteRecord(path, sessionId);
        if (!markerWritten && !recordWritten)
        {
            // Nothing would prove ownership on the next allocation, so refuse now
            // instead of leaving a folder the session cannot reuse.
            throw new IOException("scratch_workspace_unrecorded");
        }

        return path;
    }

    /// <summary>Writes the in-workspace marker, overwriting a damaged one this session owns.</summary>
    private bool TryWriteMarker(string path, string sessionId)
    {
        var marker = JsonSerializer.Serialize(new Marker(MarkerVersion, sessionId));
        var markerPath = Path.Combine(path, MarkerFileName);
        try
        {
            using var stream = new FileStream(markerPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(marker);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(error, "scratch workspace marker could not be written for {SessionId}", sessionId);
            return false;
        }
    }

    /// <summary>Writes the Host-owned record outside the workspace; bookkeeping, never a reason to fail a turn.</summary>
    private bool TryWriteRecord(string workspacePath, string sessionId)
    {
        var recordPath = RecordPath(sessionId);
        var temp = recordPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recordPath)!);
            File.WriteAllText(temp, JsonSerializer.Serialize(
                new OwnershipRecord(MarkerVersion, sessionId, Path.TrimEndingDirectorySeparator(workspacePath))));
            File.Move(temp, recordPath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger?.LogWarning(error, "scratch workspace record could not be written for {SessionId}", sessionId);
            TryDeleteFile(temp);
            return false;
        }
    }

    /// <summary>
    /// Proves that a canonical workspace root is the folder allocated to this
    /// exact session. A path-shaped match or a marker behind a reparse point is
    /// insufficient provenance for automatic scratch trust.
    /// </summary>
    public bool OwnsCanonicalWorkspace(string? canonicalWorkspacePath, string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(canonicalWorkspacePath) || !IsValidSessionId(sessionId))
        {
            return false;
        }

        var path = PathFor(sessionId!);
        try
        {
            if (!Directory.Exists(path)
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0
                || !MarkerMatches(path, sessionId!))
            {
                return false;
            }

            var expectedCanonical = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(path);
            var actualCanonical = WindowsWorkspaceIdentityResolver.GetCanonicalRoot(canonicalWorkspacePath);
            return string.Equals(
                Path.TrimEndingDirectorySeparator(expectedCanonical),
                Path.TrimEndingDirectorySeparator(actualCanonical),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public string PathFor(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
        {
            throw new ArgumentException("invalid_session_id", nameof(sessionId));
        }

        return Path.Combine(Root, sessionId);
    }

    /// <summary>Where the Host keeps its own proof that this session owns that folder.</summary>
    public string RecordPathFor(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
        {
            throw new ArgumentException("invalid_session_id", nameof(sessionId));
        }

        return RecordPath(sessionId);
    }

    /// <summary>
    /// True only for a direct child of <see cref="Root"/> whose name is a valid session id.
    /// This describes path shape only; use <see cref="OwnsCanonicalWorkspace"/> when ownership
    /// or trust depends on the scratch classification.
    /// </summary>
    public bool IsScratchPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;
        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var parent = Path.GetDirectoryName(full);
        if (parent is null
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                Path.TrimEndingDirectorySeparator(Root),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsValidSessionId(Path.GetFileName(full));
    }

    /// <summary>
    /// True only for a scratch path this session owns: the Host's own record or the
    /// marker proves it allocated the folder. A marker-only folder predates records,
    /// and routing it through <see cref="Ensure"/> is what writes its record, so a
    /// later damaged marker can still be repaired. A path-shaped child neither
    /// witness claims could be a user's folder, so the Host keeps treating it as an
    /// explicit project instead.
    /// </summary>
    public bool IsOwnedScratchPathFor(string? path, string sessionId)
    {
        if (!IsScratchPathFor(path, sessionId))
        {
            return false;
        }

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!));
        return RecordMatches(full, sessionId)
            || (IsPlainDirectory(full) && MarkerMatches(full, sessionId));
    }

    /// <summary>
    /// A marker read through a link or junction belongs to the link's target, not to
    /// Ru, so only a plain directory's marker can stand in for a missing record.
    /// </summary>
    private static bool IsPlainDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Path shape only: a direct child of <see cref="Root"/> named after this
    /// session. Ownership needs <see cref="IsOwnedScratchPathFor"/>.
    /// </summary>
    private bool IsScratchPathFor(string? path, string sessionId) =>
        IsValidSessionId(sessionId)
        && IsScratchPath(path)
        && string.Equals(
            Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!))),
            sessionId,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes one session's scratch folder. Ownership is proven by the marker or
    /// the Host's record, so a marker the agent damaged no longer keeps a deleted
    /// session's files on disk. A folder neither witness claims, even one a user
    /// pointed at under the scratch root, is never removed. True also means
    /// "nothing left to delete", so a retry after a partial cleanup succeeds.
    /// </summary>
    public bool TryDelete(string sessionId)
    {
        if (!IsValidSessionId(sessionId))
        {
            return false;
        }

        var path = Path.Combine(Root, sessionId);
        if (!Directory.Exists(path))
        {
            TryDeleteFile(RecordPath(sessionId));
            return true;
        }

        var markerPath = Path.Combine(path, MarkerFileName);
        if (!MarkerMatches(path, sessionId))
        {
            if (MarkerNamesAnotherSession(path, sessionId))
            {
                // A live marker for another session is a conflict the Host never
                // resolves automatically, even when its own record claims the folder.
                _logger?.LogWarning("scratch workspace {SessionId} has a foreign marker; refusing to delete it", sessionId);
                return false;
            }

            if (!RecordMatches(path, sessionId))
            {
                if (!File.Exists(markerPath))
                {
                    // No witness means Ru never created this folder: leave it alone, and report the
                    // delete as having nothing left to do. Refusing here would make a user folder
                    // block the session delete forever.
                    TryDeleteFile(RecordPath(sessionId));
                    return true;
                }

                _logger?.LogWarning("scratch workspace {SessionId} has an unreadable marker and no record; refusing to delete it", sessionId);
                return false;
            }
        }

        try
        {
            // The marker is the retry key: it is removed last so an interrupted or locked
            // delete still proves ownership on the next attempt.
            var deleted = TryDeleteDirectory(path, depth: 0, markerPath: Path.Combine(path, MarkerFileName));
            if (deleted)
            {
                TryDeleteFile(RecordPath(sessionId));
            }

            return deleted;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger?.LogWarning(error, "scratch workspace {SessionId} could not be deleted", sessionId);
            return false;
        }
    }

    /// <summary>
    /// Deletes scratch folders whose session no longer exists. This is the crash, bulk-delete and
    /// manual-cleanup backstop: anything the per-session path missed is reclaimed on the next Host
    /// start. Returns the number of folders removed.
    /// </summary>
    public int DeleteOrphans(Func<string, bool> sessionExists)
    {
        ArgumentNullException.ThrowIfNull(sessionExists);
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        var deleted = 0;
        var scanned = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                if (++scanned > MaxSweepDirectories)
                {
                    _logger?.LogWarning("scratch workspace sweep stopped at {Limit} directories", MaxSweepDirectories);
                    break;
                }

                var sessionId = Path.GetFileName(directory);
                if (IsValidSessionId(sessionId) && !sessionExists(sessionId))
                {
                    var path = Path.Combine(Root, sessionId);
                    if (TryDelete(sessionId) && !Directory.Exists(path))
                    {
                        deleted++;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _logger?.LogWarning(error, "scratch workspace sweep could not enumerate {Root}", Root);
        }

        SweepRecords(sessionExists);
        return deleted;
    }

    /// <summary>
    /// Asynchronous form used by the startup sweeper: the caller answers "does this session still
    /// exist" without materializing the session list, so the 500-session listing bound can never
    /// turn a live session into an orphan.
    /// </summary>
    public async Task<int> DeleteOrphansAsync(
        Func<string, CancellationToken, Task<bool>> sessionExists,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sessionExists);
        if (!Directory.Exists(Root))
        {
            return 0;
        }

        var deleted = 0;
        var scanned = 0;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > MaxSweepDirectories)
                {
                    _logger?.LogWarning("scratch workspace sweep stopped at {Limit} directories", MaxSweepDirectories);
                    break;
                }

                var sessionId = Path.GetFileName(directory);
                if (!IsValidSessionId(sessionId))
                {
                    continue;
                }

                if (await sessionExists(sessionId, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                var path = Path.Combine(Root, sessionId);
                if (TryDelete(sessionId) && !Directory.Exists(path))
                {
                    deleted++;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _logger?.LogWarning(error, "scratch workspace sweep could not enumerate {Root}", Root);
        }

        await SweepRecordsAsync(sessionExists, cancellationToken).ConfigureAwait(false);
        return deleted;
    }

    /// <summary>
    /// Drops records whose session is gone and whose folder no longer exists. A
    /// record that still has a folder is left to <see cref="TryDelete"/>, which
    /// proves ownership before removing anything.
    /// </summary>
    private int SweepRecords(Func<string, bool> sessionExists)
    {
        var owners = Path.Combine(Root, OwnersDirectoryName);
        var removed = 0;
        var scanned = 0;
        try
        {
            if (!Directory.Exists(owners))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(owners, "*.json"))
            {
                if (++scanned > MaxSweepDirectories)
                {
                    break;
                }

                var sessionId = Path.GetFileNameWithoutExtension(file);
                if (!IsValidSessionId(sessionId) || sessionExists(sessionId)
                    || Directory.Exists(Path.Combine(Root, sessionId)))
                {
                    continue;
                }

                TryDeleteFile(file);
                removed++;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _logger?.LogWarning(error, "scratch workspace record sweep could not enumerate {Root}", Root);
        }

        return removed;
    }

    private async Task<int> SweepRecordsAsync(
        Func<string, CancellationToken, Task<bool>> sessionExists,
        CancellationToken cancellationToken)
    {
        var owners = Path.Combine(Root, OwnersDirectoryName);
        var removed = 0;
        var scanned = 0;
        try
        {
            if (!Directory.Exists(owners))
            {
                return 0;
            }

            foreach (var file in Directory.EnumerateFiles(owners, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > MaxSweepDirectories)
                {
                    break;
                }

                var sessionId = Path.GetFileNameWithoutExtension(file);
                if (!IsValidSessionId(sessionId)
                    || await sessionExists(sessionId, cancellationToken).ConfigureAwait(false)
                    || Directory.Exists(Path.Combine(Root, sessionId)))
                {
                    continue;
                }

                TryDeleteFile(file);
                removed++;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            _logger?.LogWarning(error, "scratch workspace record sweep could not enumerate {Root}", Root);
        }

        return removed;
    }

    /// <summary>True when the session has a scratch folder on disk, regardless of its marker.</summary>
    public bool Exists(string sessionId) =>
        IsValidSessionId(sessionId) && Directory.Exists(Path.Combine(Root, sessionId));

    private string RecordPath(string sessionId) =>
        Path.Combine(Root, OwnersDirectoryName, sessionId + ".json");

    /// <summary>
    /// Proves from the Host's own record that this folder path was allocated to this
    /// session. The recorded path is compared, not just the session id, so a record
    /// left behind by a data root that moved does not claim the new location. It does
    /// not tell two folders at the same path apart; a link or junction swapped in there
    /// is refused by the reparse-point checks instead.
    /// </summary>
    private bool RecordMatches(string path, string sessionId)
    {
        var recordPath = RecordPath(sessionId);
        try
        {
            var info = new FileInfo(recordPath);
            if (!info.Exists || info.Length > MaxRecordBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var record = JsonSerializer.Deserialize<OwnershipRecord>(File.ReadAllText(recordPath));
            return record is { Version: MarkerVersion } && !string.IsNullOrWhiteSpace(record.Path)
                && string.Equals(record.SessionId, sessionId, StringComparison.Ordinal)
                && string.Equals(
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(record.Path)),
                    Path.TrimEndingDirectorySeparator(path),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A leftover record is bounded bookkeeping; the next sweep retries it.
        }
    }

    // Kept private: the sweep and delete paths enforce ownership before touching a folder.
    private bool MarkerMatches(string path, string sessionId) =>
        ReadMarker(path) is { } marker
        && string.Equals(marker.SessionId, sessionId, StringComparison.Ordinal);

    /// <summary>True when a readable marker claims a different session: a conflict, never relabelled.</summary>
    private bool MarkerNamesAnotherSession(string path, string sessionId) =>
        ReadMarker(path) is { } marker
        && !string.Equals(marker.SessionId, sessionId, StringComparison.Ordinal);

    private static Marker? ReadMarker(string path)
    {
        var markerPath = Path.Combine(path, MarkerFileName);
        try
        {
            var info = new FileInfo(markerPath);
            if (!info.Exists || info.Length > MaxMarkerBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            return JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath)) is { Version: MarkerVersion } marker
                ? marker
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Bounded recursive delete that never follows a reparse point: a junction or symlink the agent
    /// created is removed as a link, not traversed into its target. A locked file leaves the folder
    /// behind so a later delete or the sweeper can finish the work.
    /// </summary>
    private static bool TryDeleteDirectory(string path, int depth, string? markerPath)
    {
        if (depth > MaxDeleteDepth)
        {
            return false;
        }

        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            return true;
        }

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            // Delete the link itself; never recurse into the target it points at.
            Directory.Delete(path, recursive: false);
            return true;
        }

        foreach (var entry in info.EnumerateFileSystemInfos())
        {
            if (markerPath is not null
                && string.Equals(entry.FullName, markerPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (entry is DirectoryInfo link)
                {
                    Directory.Delete(link.FullName, recursive: false);
                }
                else
                {
                    entry.Attributes = FileAttributes.Normal;
                    entry.Delete();
                }

                continue;
            }

            if (entry is DirectoryInfo directory)
            {
                if (!TryDeleteDirectory(directory.FullName, depth + 1, markerPath: null))
                {
                    return false;
                }
            }
            else
            {
                entry.Attributes = FileAttributes.Normal;
                entry.Delete();
            }
        }

        if (markerPath is not null && File.Exists(markerPath))
        {
            File.SetAttributes(markerPath, FileAttributes.Normal);
            File.Delete(markerPath);
        }

        info.Attributes = FileAttributes.Normal;
        info.Delete();
        return true;
    }

    private sealed record Marker(int Version, string SessionId);

    /// <summary>
    /// The Host's own ownership record. <see cref="Path"/> binds the record to the
    /// folder's location under this data root.
    /// </summary>
    private sealed record OwnershipRecord(int Version, string SessionId, string Path);
}
