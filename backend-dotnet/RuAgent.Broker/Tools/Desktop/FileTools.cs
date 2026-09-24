using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Files;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Desktop;

public sealed class FileEditException : Exception
{
    public FileEditException(string code) : base(code) => Code = code;

    public FileEditException(string code, JsonObject details) : base(code)
    {
        Code = code;
        Details = details;
    }

    public string Code { get; }

    public JsonObject? Details { get; }
}

public sealed class FileTools
{
    private const long MaxEditBytes = 4 * 1024 * 1024;
    private const int MaxWriteBytes = 256 * 1024;
    private const int MaxEditOccurrences = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly WorkspacePathGuard _workspace;
    private readonly CanonicalPathResolver _resolver;
    private readonly FileListAdapter _listAdapter;
    private readonly FileReadAdapter _readAdapter;

    public FileTools(WorkspacePathGuard workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _resolver = new CanonicalPathResolver(_workspace.Root);
        _listAdapter = new FileListAdapter(_workspace.Root);
        _readAdapter = new FileReadAdapter(_workspace.Root);
    }

    public JsonObject List(string relativePath)
    {
        var args = new JsonObject { ["path"] = relativePath };
        return _listAdapter.Execute(args);
    }

    public JsonObject Read(string relativePath, int maxBytes = 256 * 1024)
    {
        if (maxBytes is < 1 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        var limit = Math.Min(maxBytes, FileToolLimits.MaxReadBytes);
        var args = new JsonObject
        {
            ["path"] = relativePath,
            ["limit"] = limit,
            ["offset"] = 0,
        };
        return _readAdapter.Execute(args);
    }

    public JsonObject Write(string relativePath, string content, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(content);
        byte[] bytes;
        try
        {
            bytes = StrictUtf8.GetBytes(content);
        }
        catch (EncoderFallbackException error)
        {
            throw new ArgumentException("invalid_utf8", error);
        }

        if (bytes.Length > MaxWriteBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        var result = FileMutationGuard.WithLock(() =>
        {
            var path = ResolveMutationPath(relativePath);
            var beforeHash = FileHashService.GetCurrentHash(path);
            if (!overwrite && !FileHashService.IsMissing(beforeHash))
            {
                throw new IOException("file_exists");
            }

            try
            {
                // Keep the final preimage check and replacement inside the shared
                // mutation gate. This closes the legacy v1 path's TOCTOU window
                // against other in-process adapters and preserves create-only
                // semantics when overwrite is false.
                FileMutationGuard.ReplaceIfHashMatches(path, bytes, beforeHash, overwrite);
            }
            catch (IOException error) when (!overwrite && File.Exists(path))
            {
                throw new IOException("file_exists", error);
            }

            return (path, bytes.Length);
        });

        return new JsonObject { ["path"] = _workspace.ToRelative(result.path), ["bytes"] = result.Length };
    }

    public JsonObject Edit(string relativePath, string? oldString, string? newString, int expectedOccurrences = 1)
    {
        if (string.IsNullOrEmpty(oldString))
        {
            throw new FileEditException("old_string_empty");
        }

        // The v1 schema rejects values below one before dispatch. Keep the
        // legacy adapter's historical mismatch response for a direct caller
        // passing zero, while still bounding pathological values.
        if (expectedOccurrences > MaxEditOccurrences)
        {
            throw new FileEditException("invalid_expected_occurrences");
        }

        try
        {
            if (StrictUtf8.GetByteCount(oldString) > MaxEditBytes
                || StrictUtf8.GetByteCount(newString ?? string.Empty) > MaxEditBytes)
            {
                throw new FileEditException("content_too_large");
            }
        }
        catch (EncoderFallbackException error)
        {
            throw new FileEditException("invalid_utf8", new JsonObject { ["message"] = error.Message });
        }

        var result = FileMutationGuard.WithLock(() =>
        {
            var path = ResolveMutationPath(relativePath);
            if (!File.Exists(path))
            {
                throw new FileEditException("file_not_found");
            }

            if (new FileInfo(path).Length > MaxEditBytes)
            {
                throw new FileEditException("file_too_large");
            }

            var beforeHash = FileHashService.GetCurrentHash(path);
            var read = ReadAtMost(path, checked((int)MaxEditBytes));
            if (read.Truncated)
            {
                throw new FileEditException("file_too_large");
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(read.Bytes);
            }
            catch (DecoderFallbackException error)
            {
                throw new FileEditException("invalid_utf8", new JsonObject { ["message"] = error.Message });
            }

            // The metadata hash and byte read must describe the same preimage.
            // Otherwise a concurrent writer could cause us to edit stale content.
            if (!string.Equals(FileHashService.ComputeBytesHash(read.Bytes), beforeHash, StringComparison.Ordinal))
            {
                throw new FileEditException("workspace_changed");
            }

            var useCrLf = text.Contains("\r\n", StringComparison.Ordinal);
            var normalizedText = NormalizeToLf(text);
            var normalizedOld = NormalizeToLf(oldString);
            var normalizedNew = NormalizeToLf(newString ?? string.Empty);
            var matches = CountOccurrences(normalizedText, normalizedOld);
            if (matches != expectedOccurrences)
            {
                throw new FileEditException("edit_not_unique", new JsonObject { ["matches"] = matches });
            }

            var updated = DenormalizeFromLf(
                normalizedText.Replace(normalizedOld, normalizedNew, StringComparison.Ordinal),
                useCrLf);
            byte[] updatedBytes;
            try
            {
                updatedBytes = StrictUtf8.GetBytes(updated);
            }
            catch (EncoderFallbackException error)
            {
                throw new FileEditException("invalid_utf8", new JsonObject { ["message"] = error.Message });
            }

            if (updatedBytes.Length > MaxEditBytes)
            {
                throw new FileEditException("content_too_large");
            }

            FileMutationGuard.ReplaceIfHashMatches(path, updatedBytes, beforeHash);
            return (path, matches);
        });

        return new JsonObject { ["path"] = _workspace.ToRelative(result.path), ["replacements"] = result.matches };
    }

    private string ResolveMutationPath(string relativePath)
    {
        // Keep the legacy guard's input validation and symlink-escape checks, then
        // use the canonical resolver so protected paths and junctions are classified
        // consistently with the v2 adapters.
        _workspace.Resolve(relativePath, allowMissing: true);
        ResolvedPath resolved;
        try
        {
            resolved = _resolver.Resolve(relativePath);
        }
        catch (ArgumentException error)
        {
            throw new UnauthorizedAccessException("workspace_path_invalid", error);
        }

        if (resolved.IsOutsideWorkspace)
        {
            throw new UnauthorizedAccessException("workspace_path_outside_root");
        }

        if (resolved.IsProtected)
        {
            throw new UnauthorizedAccessException("protected_path");
        }

        return resolved.CanonicalPath;
    }

    private static BoundedRead ReadAtMost(string path, int maxBytes)
    {
        var buffer = new byte[checked(maxBytes + 1)];
        var total = 0;
        using var stream = HardLinkGuard.OpenRead(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);
        while (total < buffer.Length)
        {
            var count = stream.Read(buffer, total, buffer.Length - total);
            if (count == 0)
            {
                break;
            }

            total += count;
        }

        var truncated = total > maxBytes;
        Array.Resize(ref buffer, Math.Min(total, maxBytes));
        return new BoundedRead(buffer, truncated);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string NormalizeToLf(string text) => text.Replace("\r\n", "\n").Replace("\r", "\n");

    private static string DenormalizeFromLf(string text, bool useCrLf) =>
        useCrLf ? text.Replace("\n", "\r\n") : text;

    private readonly record struct BoundedRead(byte[] Bytes, bool Truncated);
}
