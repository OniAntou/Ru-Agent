using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileRestoreAdapter
{
    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;
    private readonly int _maxRecoveryBytes;

    // In-memory registry for Broker-level tests (process-isolated). For the persistence-backed Host path, FileRecoveryStore is used directly.
    private static readonly ConcurrentDictionary<string, RecoveryRecord> _store = new();
    // ExecuteWithBytes is the adapter boundary used by persistence-backed hosts. Keep
    // a short-lived replay ledger for those opaque ids as well; otherwise a caller could
    // invoke that public API repeatedly with the same recovery bytes.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> _externalConsumed = new();
    private static readonly TimeSpan ExternalConsumedTtl = TimeSpan.FromMinutes(30);
    private const int MaxRecoveryRecords = 4096;

    private sealed record RecoveryRecord(
        string CanonicalPath,
        byte[] Content,
        string Sha256,
        DateTimeOffset CreatedAt,
        bool IsConsumed);

    public FileRestoreAdapter(string workspaceRoot, int maxRecoveryBytes = FileToolLimits.MaxRecoveryBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot);
        _maxRecoveryBytes = FileToolLimits.ValidateRecoveryBytes(maxRecoveryBytes);
    }

    // For testing: allow injecting a recovery directly (simulates Host gateway having stored recovery)
    internal static string RegisterForTest(string canonicalPath, byte[] content, string sha256)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateRecoveryHash(content, sha256);
        PruneStore(DateTimeOffset.UtcNow);
        if (content.Length > FileDeleteAdapter.MaxRecoveryBytes)
        {
            throw new ArgumentException("recovery_too_large", nameof(content));
        }

        var id = "payload_" + Guid.NewGuid().ToString("N");
        var record = new RecoveryRecord(canonicalPath, (byte[])content.Clone(), sha256, DateTimeOffset.UtcNow, false);
        _store[id] = record;
        PruneStore(DateTimeOffset.UtcNow);
        return id;
    }

    internal static void RegisterWithId(string recoveryId, string canonicalPath, byte[] content, string sha256, int maxRecoveryBytes = FileToolLimits.MaxRecoveryBytes)
    {
        ArgumentNullException.ThrowIfNull(content);
        ValidateRecoveryHash(content, sha256);
        PruneStore(DateTimeOffset.UtcNow);
        if (content.Length > FileToolLimits.ValidateRecoveryBytes(maxRecoveryBytes))
        {
            throw new ArgumentException("recovery_too_large", nameof(content));
        }

        var record = new RecoveryRecord(canonicalPath, (byte[])content.Clone(), sha256, DateTimeOffset.UtcNow, false);
        _store[recoveryId] = record;
        PruneStore(DateTimeOffset.UtcNow);
    }

    internal static void ClearForTest()
    {
        _store.Clear();
        _externalConsumed.Clear();
    }

    internal static bool IsAvailable(string recoveryId)
    {
        PruneStore(DateTimeOffset.UtcNow);
        return _store.TryGetValue(recoveryId, out var rec)
            && !rec.IsConsumed;
    }

    /// <summary>
    /// Peeks the restore target without reserving or consuming the record, so
    /// the dispatcher can select the session-granted adapter root for an
    /// external restore. Returns false for unknown, consumed, or missing ids;
    /// callers then fall back to the lease toolset, which raises the native
    /// recovery error. Never throws.
    /// </summary>
    internal static bool TryGetCanonicalPath(string? recoveryId, out string? canonicalPath)
    {
        canonicalPath = null;
        if (string.IsNullOrWhiteSpace(recoveryId))
        {
            return false;
        }

        if (_store.TryGetValue(recoveryId, out var record) && !record.IsConsumed)
        {
            canonicalPath = record.CanonicalPath;
            return true;
        }

        return false;
    }

    public JsonObject Prepare(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        PruneStore(DateTimeOffset.UtcNow);
        var recoveryId = GetRequiredString(arguments, "recovery_id");

        if (!_store.TryGetValue(recoveryId, out var record))
        {
            throw new FileNotFoundException("recovery_unavailable");
        }

        if (record.IsConsumed)
        {
            throw new InvalidOperationException("recovery_consumed");
        }

        // Never perform a filesystem mutation from an inconsistent recovery
        // record. This also protects the test/persistence seam if a corrupted
        // payload or hash reaches the adapter.
        ValidateRecoveryHash(record.Content, record.Sha256);

        var resolved = _resolver.Resolve(record.CanonicalPath);

        if (resolved.IsProtected || resolved.IsOutsideWorkspace)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        HardLinkGuard.RequireSingleLink(resolved.CanonicalPath);
        var safe = new JsonObject
        {
            ["recovery_id"] = recoveryId,
            ["path"] = ToRelative(resolved.CanonicalPath),
            ["bytes"] = record.Content.Length,
            ["sha256"] = record.Sha256,
        };
        return safe;
    }

    public JsonObject Execute(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        PruneStore(DateTimeOffset.UtcNow);
        var recoveryId = GetRequiredString(arguments, "recovery_id");

        if (!_store.TryGetValue(recoveryId, out var record))
        {
            throw new FileNotFoundException("recovery_unavailable");
        }

        // Reserve the one-time recovery before inspecting or writing the target.
        // A check followed by a later assignment lets concurrent restores both
        // write the same payload.
        if (record.IsConsumed)
        {
            throw new InvalidOperationException("recovery_consumed");
        }

        var reserved = record with { IsConsumed = true };
        if (!_store.TryUpdate(recoveryId, reserved, record))
        {
            throw new InvalidOperationException("recovery_consumed");
        }

        var reservationHeld = true;

        try
        {
            var restored = RestoreAtomically(
                record.CanonicalPath,
                record.CanonicalPath,
                record.Content,
                requireExistingParent: false);
            var resolved = restored.Resolved;
            var restoredBytes = restored.Bytes;
            var restoredHash = FileHashService.ComputeBytesHash(restoredBytes);
            if (!string.Equals(restoredHash, record.Sha256, StringComparison.Ordinal))
            {
                throw new IOException("io_error");
            }

            reservationHeld = false;
            // One-time contract: the record is removed after a successful restore so
            // a replay fails as unavailable, matching the Host recovery store.
            _store.TryRemove(recoveryId, out _);

            return new JsonObject
            {
                ["recovery_id"] = recoveryId,
                ["path"] = ToRelative(resolved.CanonicalPath),
                ["bytes"] = restoredBytes.Length,
                ["restored_sha256"] = restoredHash,
            };
        }
        catch
        {
            if (reservationHeld)
            {
                _store.TryUpdate(recoveryId, record, reserved);
            }

            throw;
        }
    }

    // For Host gateway with persistence: this method allows restoring from provided bytes directly, bypassing in-memory store
    public JsonObject ExecuteWithBytes(JsonObject arguments, byte[] recoveryBytes, string expectedSha256, string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(recoveryBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSha256);
        if (recoveryBytes.Length > _maxRecoveryBytes)
        {
            throw new ArgumentException("recovery_too_large", nameof(recoveryBytes));
        }
        // The caller owns the input array. Clone it before hashing or storing it so
        // a concurrent caller mutation cannot make the approved hash and bytes
        // diverge during the restore operation.
        var payload = (byte[])recoveryBytes.Clone();
        var recoveryId = GetRequiredString(arguments, "recovery_id");
        if (recoveryId.Length > 256)
        {
            throw new ArgumentException("invalid_recovery_id");
        }

        if (!FileHashService.IsValidSha256(expectedSha256))
        {
            throw new ArgumentException("invalid_expected_sha256");
        }

        // Do not let this persistence-backed overload bypass the adapter's own
        // workspace classification. It accepts an absolute path by design, so the
        // resolved identity must still be inside the configured root and unprotected.
        var resolved = _resolver.Resolve(canonicalPath);
        if (resolved.IsProtected || resolved.IsOutsideWorkspace)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        var computed = FileHashService.ComputeBytesHash(payload);
        if (!string.Equals(computed, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("recovery_unavailable");
        }

        // A recovery id owned by the in-memory registry must be consumed through the
        // normal path, never through a caller-supplied byte array.
        if (_store.ContainsKey(recoveryId))
        {
            throw new InvalidOperationException("recovery_unavailable");
        }

        PruneExternalConsumed(DateTimeOffset.UtcNow);
        if (!_externalConsumed.TryAdd(recoveryId, DateTimeOffset.UtcNow))
        {
            throw new InvalidOperationException("recovery_consumed");
        }

        var reservationHeld = true;
        try
        {
            var restored = RestoreAtomically(
                canonicalPath,
                resolved.CanonicalPath,
                payload,
                requireExistingParent: true);
            resolved = restored.Resolved;
            var restoredBytes = restored.Bytes;
            var restoredHash = FileHashService.ComputeBytesHash(restoredBytes);
            if (!string.Equals(restoredHash, expectedSha256, StringComparison.Ordinal))
            {
                throw new IOException("io_error");
            }

            reservationHeld = false;
            return new JsonObject
            {
                ["recovery_id"] = recoveryId,
                ["path"] = ToRelative(resolved.CanonicalPath),
                ["bytes"] = payload.Length,
                ["restored_sha256"] = restoredHash,
            };
        }
        finally
        {
            if (reservationHeld)
            {
                _externalConsumed.TryRemove(recoveryId, out _);
            }
        }
    }

    private static void PruneExternalConsumed(DateTimeOffset now)
    {
        var cutoff = now - ExternalConsumedTtl;
        foreach (var entry in _externalConsumed)
        {
            if (entry.Value <= cutoff)
            {
                _externalConsumed.TryRemove(new KeyValuePair<string, DateTimeOffset>(entry.Key, entry.Value));
            }
        }

        if (_externalConsumed.Count <= MaxRecoveryRecords)
        {
            return;
        }

        foreach (var entry in _externalConsumed
                     .OrderBy(pair => pair.Value)
                     .Take(_externalConsumed.Count - MaxRecoveryRecords))
        {
            _externalConsumed.TryRemove(new KeyValuePair<string, DateTimeOffset>(entry.Key, entry.Value));
        }
    }

    private (ResolvedPath Resolved, byte[] Bytes) RestoreAtomically(
        string inputPath,
        string expectedCanonicalPath,
        byte[] content,
        bool requireExistingParent)
    {
        return FileMutationGuard.WithLock(() =>
        {
            ResolvedPath resolved;
            try
            {
                resolved = _resolver.Resolve(inputPath);
            }
            catch (ArgumentException ex)
            {
                throw new UnauthorizedAccessException("workspace_changed", ex);
            }

            if (!string.Equals(resolved.CanonicalPath, expectedCanonicalPath, StringComparison.OrdinalIgnoreCase)
                || resolved.IsProtected
                || resolved.IsOutsideWorkspace)
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            HardLinkGuard.RequireSingleLink(resolved.CanonicalPath);

            // Original target must still be missing; never clobber a concurrent
            // user write or directory created after preparation.
            if (File.Exists(resolved.CanonicalPath) || Directory.Exists(resolved.CanonicalPath))
            {
                throw new InvalidOperationException("recovery_target_exists");
            }

            var directory = Path.GetDirectoryName(resolved.CanonicalPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new DirectoryNotFoundException("target_directory_missing");
            }

            if (requireExistingParent)
            {
                if (!Directory.Exists(directory))
                {
                    throw new DirectoryNotFoundException("target_directory_missing");
                }

                var parent = _resolver.Resolve(directory);
                if (parent.IsProtected
                    || parent.IsOutsideWorkspace
                    || !string.Equals(parent.CanonicalPath, directory, StringComparison.OrdinalIgnoreCase))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }
            }
            else
            {
                Directory.CreateDirectory(directory);
            }

            try
            {
                AtomicFileReplacement.Replace(resolved.CanonicalPath, content, overwrite: false);
            }
            catch (IOException ex) when (
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || File.Exists(resolved.CanonicalPath)
                || Directory.Exists(resolved.CanonicalPath))
            {
                throw new InvalidOperationException("recovery_target_exists", ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException("access_denied", ex);
            }

            var restoredBytes = ReadBoundedBytes(resolved.CanonicalPath, _maxRecoveryBytes);
            if (!restoredBytes.SequenceEqual(content))
            {
                throw new IOException("io_error");
            }

            return (resolved, restoredBytes);
        });
    }

    private static void PruneStore(DateTimeOffset now)
    {
        var cutoff = now - TimeSpan.FromMinutes(30);
        foreach (var entry in _store)
        {
            if (entry.Value.CreatedAt <= cutoff)
            {
                _store.TryRemove(new KeyValuePair<string, RecoveryRecord>(entry.Key, entry.Value));
            }
        }

        if (_store.Count <= MaxRecoveryRecords)
        {
            return;
        }

        foreach (var entry in _store
                     .OrderBy(pair => pair.Value.CreatedAt)
                     .Take(_store.Count - MaxRecoveryRecords))
        {
            _store.TryRemove(new KeyValuePair<string, RecoveryRecord>(entry.Key, entry.Value));
        }
    }

    private static void ValidateRecoveryHash(byte[] content, string sha256)
    {
        if (!FileHashService.IsValidSha256(sha256)
            || !string.Equals(FileHashService.ComputeBytesHash(content), sha256, StringComparison.Ordinal))
        {
            throw new ArgumentException("invalid_recovery_hash");
        }
    }

    private static byte[] ReadBoundedBytes(string canonicalPath, int maxRecoveryBytes)
    {
        using var stream = HardLinkGuard.OpenRead(canonicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maxRecoveryBytes, 81920));
        var buffer = new byte[Math.Min(maxRecoveryBytes, 81920)];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length > maxRecoveryBytes - read)
            {
                throw new ArgumentException("recovery_too_large");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private string ToRelative(string canonicalPath)
    {
        var relative = Path.GetRelativePath(_resolver.CanonicalRoot, canonicalPath);
        if (relative == ".")
        {
            return ".";
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static string GetRequiredString(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s is not null && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        throw new ArgumentException($"missing_{key}");
    }
}
