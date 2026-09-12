using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileDeleteAdapter
{
    public const int MaxRecoveryBytes = 4 * 1024 * 1024;

    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;
    private readonly int _maxRecoveryBytes;

    public FileDeleteAdapter(string workspaceRoot, int maxRecoveryBytes = FileToolLimits.MaxRecoveryBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot);
        _maxRecoveryBytes = FileToolLimits.ValidateRecoveryBytes(maxRecoveryBytes);
    }

    public FileDeletePrepareResult Prepare(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var pathInput = GetRequiredString(arguments, "path");
        var expected = GetRequiredString(arguments, "expected_sha256");

        if (pathInput.Length > FileToolLimits.MaxPathLength)
        {
            throw new ArgumentException("path_too_long");
        }

        FileHashService.ValidateExpectedSha256(expected);

        var resolved = ResolveChecked(pathInput);

        if (resolved.IsProtected || resolved.IsOutsideWorkspace)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        if (Directory.Exists(resolved.CanonicalPath))
        {
            throw new IOException("is_directory");
        }

        if (!File.Exists(resolved.CanonicalPath))
        {
            throw new FileNotFoundException("file_not_found");
        }

        var info = new FileInfo(resolved.CanonicalPath);
        if (info.Length > _maxRecoveryBytes)
        {
            throw new ArgumentException("recovery_too_large");
        }

        var currentHash = FileHashService.GetCurrentHash(resolved.CanonicalPath);
        if (!string.Equals(currentHash, expected, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        // Read file byte-exact, bounded by cap (the file may grow after the
        // metadata check, so do not trust FileInfo.Length for allocation).
        var bytes = ReadBoundedBytes(resolved.CanonicalPath, _maxRecoveryBytes);

        var computed = FileHashService.ComputeBytesHash(bytes);
        if (!string.Equals(computed, expected, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        var safe = new JsonObject
        {
            ["path"] = ToRelative(resolved.CanonicalPath),
            ["before_sha256"] = currentHash,
            ["bytes"] = bytes.Length,
        };

        return new FileDeletePrepareResult(safe, bytes, currentHash, resolved.CanonicalPath, pathInput);
    }

    public JsonObject Execute(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var pathInput = GetRequiredString(arguments, "path");
        var expected = GetRequiredString(arguments, "expected_sha256");

        if (pathInput.Length > FileToolLimits.MaxPathLength)
        {
            throw new ArgumentException("path_too_long");
        }

        FileHashService.ValidateExpectedSha256(expected);

        var resolved = ResolveChecked(pathInput);

        // Re-resolve to detect junction/symlink changes
        ResolvedPath reResolved;
        try
        {
            reResolved = _resolver.Resolve(pathInput);
        }
        catch (ArgumentException ex)
        {
            throw new UnauthorizedAccessException("workspace_changed", ex);
        }

        if (!string.Equals(reResolved.CanonicalPath, resolved.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || reResolved.Classification != resolved.Classification)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        if (reResolved.IsProtected || reResolved.IsOutsideWorkspace)
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        if (Directory.Exists(reResolved.CanonicalPath))
        {
            throw new IOException("is_directory");
        }

        if (!File.Exists(reResolved.CanonicalPath))
        {
            throw new FileNotFoundException("file_not_found");
        }

        var info = new FileInfo(reResolved.CanonicalPath);
        if (info.Length > _maxRecoveryBytes)
        {
            throw new ArgumentException("recovery_too_large");
        }

        var deletion = FileMutationGuard.WithLock(() =>
        {
            var currentHash = FileHashService.GetCurrentHash(reResolved.CanonicalPath);
            if (!string.Equals(currentHash, expected, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            // Capture the byte-exact recovery copy and perform the delete under the
            // same process-wide mutation gate. This prevents another adapter call
            // from replacing the preimage between the hash and File.Delete.
            var recoveryBytes = ReadBoundedBytes(reResolved.CanonicalPath, _maxRecoveryBytes);
            var computedHash = FileHashService.ComputeBytesHash(recoveryBytes);
            if (!string.Equals(computedHash, currentHash, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }

            File.Delete(reResolved.CanonicalPath);
            if (File.Exists(reResolved.CanonicalPath))
            {
                throw new IOException("io_error");
            }

            return (currentHash, recoveryBytes);
        });
        var currentHash = deletion.currentHash;
        var recoveryBytes = deletion.recoveryBytes;

        var recoveryId = "payload_" + Guid.NewGuid().ToString("N");
        // Store for restore – byte-exact, shared with FileRestoreAdapter
        FileRestoreAdapter.RegisterWithId(recoveryId, reResolved.CanonicalPath, recoveryBytes, currentHash, _maxRecoveryBytes);
        return new JsonObject
        {
            ["path"] = ToRelative(reResolved.CanonicalPath),
            ["before_sha256"] = currentHash,
            ["recovery_id"] = recoveryId,
            ["bytes"] = recoveryBytes.Length,
        };
    }

    // Helper for the Broker preparation path: returns recovery bytes without deleting, for preparation payload
    public (JsonObject safePreview, byte[] recoveryBytes, string beforeHash, string canonicalPath) PrepareForGateway(JsonObject arguments)
    {
        var result = Prepare(arguments);
        return (result.SafePreview, result.RecoveryBytes, result.BeforeHash, result.CanonicalPath);
    }

    private ResolvedPath ResolveChecked(string input)
    {
        try
        {
            var resolved = _resolver.Resolve(input);
            return resolved;
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("invalid_path", ex);
        }
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

    private static byte[] ReadBoundedBytes(string canonicalPath, int maxRecoveryBytes)
    {
        using var stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.SequentialScan);
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
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s is not null)
        {
            return s;
        }

        throw new ArgumentException($"missing_{key}");
    }
}

public sealed record FileDeletePrepareResult(
    JsonObject SafePreview,
    byte[] RecoveryBytes,
    string BeforeHash,
    string CanonicalPath,
    string InputPath);
