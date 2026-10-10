using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileRestoreAdapter
{
    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;
    private readonly int _maxRecoveryBytes;
    private readonly InMemoryRecoveryStore _store;

    public FileRestoreAdapter(string workspaceRoot, int maxRecoveryBytes = FileToolLimits.MaxRecoveryBytes)
        : this(workspaceRoot, new InMemoryRecoveryStore(), maxRecoveryBytes)
    {
    }

    // A shared store is required when delete and restore must observe the same
    // recovery ids (ToolExecutionService owns one per instance).
    internal FileRestoreAdapter(string workspaceRoot, InMemoryRecoveryStore store, int maxRecoveryBytes = FileToolLimits.MaxRecoveryBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(store);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot);
        _maxRecoveryBytes = FileToolLimits.ValidateRecoveryBytes(maxRecoveryBytes);
        _store = store;
    }

    public JsonObject Prepare(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var recoveryId = GetRequiredString(arguments, "recovery_id");

        if (!_store.TryGet(recoveryId, out var record))
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
        InMemoryRecoveryStore.ValidateRecoveryHash(record.Content, record.Sha256);

        var resolved = _resolver.Resolve(record.CanonicalPath);

        if (resolved.IsProtected || resolved.IsOutsideWorkspace)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        HardLinkGuard.RequireSingleLink(resolved.CanonicalPath);
        var safe = new JsonObject
        {
            ["recovery_id"] = recoveryId,
            ["path"] = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, resolved.CanonicalPath),
            ["bytes"] = record.Content.Length,
            ["sha256"] = record.Sha256,
        };
        return safe;
    }

    public JsonObject Execute(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var recoveryId = GetRequiredString(arguments, "recovery_id");

        if (!_store.TryGet(recoveryId, out var record))
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

        if (!_store.TryReserve(recoveryId, record, out var reserved))
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
            _store.TryRemove(recoveryId);

            return new JsonObject
            {
                ["recovery_id"] = recoveryId,
                ["path"] = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, resolved.CanonicalPath),
                ["bytes"] = restoredBytes.Length,
                ["restored_sha256"] = restoredHash,
            };
        }
        catch
        {
            if (reservationHeld)
            {
                _store.Release(recoveryId, record, reserved);
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
        if (_store.ContainsRecord(recoveryId))
        {
            throw new InvalidOperationException("recovery_unavailable");
        }

        if (!_store.TryConsumeExternal(recoveryId))
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
                ["path"] = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, resolved.CanonicalPath),
                ["bytes"] = payload.Length,
                ["restored_sha256"] = restoredHash,
            };
        }
        finally
        {
            if (reservationHeld)
            {
                _store.ReleaseExternal(recoveryId);
            }
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

            try
            {
                FileMutationGuard.ReplaceIfHashMatches(resolved.CanonicalPath, content, FileHashService.Missing,
                    overwrite: false);
            }
            catch (IOException ex) when (
                ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || File.Exists(resolved.CanonicalPath)
                || Directory.Exists(resolved.CanonicalPath))
            {
                throw new InvalidOperationException("recovery_target_exists", ex);
            }
            catch (UnauthorizedAccessException ex) when (ex.Message == "workspace_changed"
                && (File.Exists(resolved.CanonicalPath) || Directory.Exists(resolved.CanonicalPath)))
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

    private static string GetRequiredString(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s is not null && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        throw new ArgumentException($"missing_{key}");
    }
}
