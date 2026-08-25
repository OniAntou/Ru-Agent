using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileRestoreAdapter
{
    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;

    // In-memory registry for Broker-level tests (process-isolated). For persistence-backed Host path, HarnessToolGateway uses FileRecoveryStore directly.
    private static readonly ConcurrentDictionary<string, RecoveryRecord> _store = new();

    private sealed record RecoveryRecord(
        string CanonicalPath,
        byte[] Content,
        string Sha256,
        DateTimeOffset CreatedAt,
        bool IsConsumed);

    public FileRestoreAdapter(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot);
    }

    // For testing: allow injecting a recovery directly (simulates Host gateway having stored recovery)
    internal static string RegisterForTest(string canonicalPath, byte[] content, string sha256)
    {
        var id = "payload_" + Guid.NewGuid().ToString("N");
        var record = new RecoveryRecord(canonicalPath, (byte[])content.Clone(), sha256, DateTimeOffset.UtcNow, false);
        _store[id] = record;
        return id;
    }

    internal static void RegisterWithId(string recoveryId, string canonicalPath, byte[] content, string sha256)
    {
        var record = new RecoveryRecord(canonicalPath, (byte[])content.Clone(), sha256, DateTimeOffset.UtcNow, false);
        _store[recoveryId] = record;
    }

    internal static void ClearForTest()
    {
        _store.Clear();
    }

    internal static bool IsAvailable(string recoveryId) =>
        _store.TryGetValue(recoveryId, out var rec) && !rec.IsConsumed;

    public JsonObject Prepare(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var recoveryId = GetRequiredString(arguments, "recovery_id");

        if (!_store.TryGetValue(recoveryId, out var record))
        {
            throw new FileNotFoundException("recovery_unavailable");
        }

        if (record.IsConsumed)
        {
            throw new InvalidOperationException("recovery_consumed");
        }

        var resolved = _resolver.Resolve(record.CanonicalPath);

        if (resolved.IsProtected || resolved.IsOutsideWorkspace)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

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
        var recoveryId = GetRequiredString(arguments, "recovery_id");

        if (!_store.TryGetValue(recoveryId, out var record))
        {
            throw new FileNotFoundException("recovery_unavailable");
        }

        // Check consumed first – replay returns recovery_consumed
        if (record.IsConsumed)
        {
            throw new InvalidOperationException("recovery_consumed");
        }

        var resolved = _resolver.Resolve(record.CanonicalPath);

        if (resolved.IsProtected || resolved.IsOutsideWorkspace)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        // Original target must still be missing; recreated/changed rejects
        if (File.Exists(resolved.CanonicalPath) || Directory.Exists(resolved.CanonicalPath))
        {
            throw new InvalidOperationException("recovery_target_exists");
        }

        // Ensure directory exists
        var dir = Path.GetDirectoryName(resolved.CanonicalPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // Atomic restore: write to temp then move
        try
        {
            var tempPath = Path.Combine(dir!, $".ru-restore-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(tempPath, record.Content);
                File.Move(tempPath, resolved.CanonicalPath, overwrite: false);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
        catch (IOException ex) when (ex.Message.Contains("already exists"))
        {
            throw new InvalidOperationException("recovery_target_exists");
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException("access_denied", ex);
        }

        // Verify byte-identity
        var restoredBytes = File.ReadAllBytes(resolved.CanonicalPath);
        if (!restoredBytes.SequenceEqual(record.Content))
        {
            throw new IOException("io_error");
        }

        var restoredHash = FileHashService.ComputeBytesHash(restoredBytes);
        if (!string.Equals(restoredHash, record.Sha256, StringComparison.Ordinal))
        {
            throw new IOException("io_error");
        }

        // Mark consumed atomically
        var updated = record with { IsConsumed = true };
        _store[recoveryId] = updated;

        return new JsonObject
        {
            ["recovery_id"] = recoveryId,
            ["path"] = ToRelative(resolved.CanonicalPath),
            ["bytes"] = restoredBytes.Length,
            ["restored_sha256"] = restoredHash,
        };
    }

    // For Host gateway with persistence: this method allows restoring from provided bytes directly, bypassing in-memory store
    public JsonObject ExecuteWithBytes(JsonObject arguments, byte[] recoveryBytes, string expectedSha256, string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(recoveryBytes);
        var recoveryId = GetRequiredString(arguments, "recovery_id");
        var resolved = _resolver.Resolve(canonicalPath);

        if (File.Exists(resolved.CanonicalPath) || Directory.Exists(resolved.CanonicalPath))
        {
            throw new InvalidOperationException("recovery_target_exists");
        }

        var dir = Path.GetDirectoryName(resolved.CanonicalPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var computed = FileHashService.ComputeBytesHash(recoveryBytes);
        if (!string.Equals(computed, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("recovery_unavailable");
        }

        File.WriteAllBytes(resolved.CanonicalPath, recoveryBytes);
        var restoredHash = FileHashService.ComputeBytesHash(File.ReadAllBytes(resolved.CanonicalPath));
        return new JsonObject
        {
            ["recovery_id"] = recoveryId,
            ["path"] = ToRelative(resolved.CanonicalPath),
            ["bytes"] = recoveryBytes.Length,
            ["restored_sha256"] = restoredHash,
        };
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
