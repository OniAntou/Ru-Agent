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
    private const int MarkerVersion = 1;
    private const int MaxMarkerBytes = 1024;
    private const int MaxSweepDirectories = 10_000;
    private const int MaxDeleteDepth = 64;

    private readonly ILogger<ScratchWorkspaceStore>? _logger;

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
    /// Creates the session's scratch folder and its marker. An existing folder is reused only
    /// when its marker already proves ownership by this session; it is never adopted or relabelled.
    /// </summary>
    public string Ensure(string sessionId)
    {
        var path = PathFor(sessionId);
        if (Directory.Exists(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                // Never write a marker through a junction: the target is not Ru's folder.
                throw new IOException("scratch_workspace_reparse");
            }

            if (!MarkerMatches(path, sessionId))
            {
                // Existing user data is not ours to claim. In particular, never
                // replace a missing, malformed or foreign ownership marker.
                throw new IOException("scratch_workspace_unowned");
            }

            return path;
        }

        Directory.CreateDirectory(path);
        var marker = JsonSerializer.Serialize(new Marker(MarkerVersion, sessionId));
        var markerPath = Path.Combine(path, MarkerFileName);
        try
        {
            using var stream = new FileStream(markerPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
            writer.Write(marker);
        }
        catch (IOException) when (MarkerMatches(path, sessionId))
        {
            // Concurrent idempotent allocation for this same session is safe only
            // after the winner's marker proves ownership.
        }

        return path;
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
    /// Deletes one session's scratch folder. Ownership is proven by the marker: a folder a user
    /// pointed at (even under the scratch root) is never removed. True also means "nothing left to
    /// delete", so a retry after a partial cleanup succeeds.
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
            return true;
        }

        var markerPath = Path.Combine(path, MarkerFileName);
        if (!File.Exists(markerPath))
        {
            // No marker means Ru never created this folder: leave it alone, and report the
            // delete as having nothing left to do. Refusing here would make a user folder
            // block the session delete forever.
            return true;
        }

        if (!MarkerMatches(path, sessionId))
        {
            _logger?.LogWarning("scratch workspace {SessionId} has a foreign or unreadable marker; refusing to delete it", sessionId);
            return false;
        }

        try
        {
            // The marker is the retry key: it is removed last so an interrupted or locked
            // delete still proves ownership on the next attempt.
            return TryDeleteDirectory(path, depth: 0, markerPath: Path.Combine(path, MarkerFileName));
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

        return deleted;
    }

    /// <summary>True when the session has a scratch folder on disk, regardless of its marker.</summary>
    public bool Exists(string sessionId) =>
        IsValidSessionId(sessionId) && Directory.Exists(Path.Combine(Root, sessionId));

    // Kept private: the sweep and delete paths enforce ownership before touching a folder.
    private bool MarkerMatches(string path, string sessionId)
    {
        var markerPath = Path.Combine(path, MarkerFileName);
        try
        {
            var info = new FileInfo(markerPath);
            if (!info.Exists || info.Length > MaxMarkerBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var marker = JsonSerializer.Deserialize<Marker>(File.ReadAllText(markerPath));
            return marker is { Version: MarkerVersion }
                && string.Equals(marker.SessionId, sessionId, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return false;
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
}
