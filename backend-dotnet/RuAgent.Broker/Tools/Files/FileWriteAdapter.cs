using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileWriteAdapter
{
    public const int MaxInputBytes = 4 * 1024 * 1024;
    public const int MaxDiffChars = 8 * 1024;

    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;

    public FileWriteAdapter(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot);
    }

    public JsonObject Prepare(JsonObject arguments)
    {
        var validated = ValidateAndResolve(arguments, out var resolved, out var expected, out var content);
        var beforeHash = FileHashService.GetCurrentHash(resolved.CanonicalPath);
        if (!string.Equals(beforeHash, expected, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        var afterHash = FileHashService.ComputeStringHash(content);
        var diff = BuildDiffSummary(
            wasMissing: FileHashService.IsMissing(beforeHash),
            beforeHash: beforeHash,
            afterHash: afterHash,
            oldContent: File.Exists(resolved.CanonicalPath) ? ReadAllTextForDiff(resolved.CanonicalPath) : string.Empty,
            newContent: content);

        return new JsonObject
        {
            ["path"] = ToRelative(resolved.CanonicalPath),
            ["before_sha256"] = beforeHash,
            ["after_sha256"] = afterHash,
            ["diff_summary"] = diff,
            ["bytes"] = Encoding.UTF8.GetByteCount(content),
        };
    }

    public JsonObject Execute(JsonObject arguments)
    {
        ResolvedPath resolved;
        string expected;
        string content;
        try
        {
            ValidateAndResolve(arguments, out resolved, out expected, out content);
        }
        catch (UnauthorizedAccessException ex) when (ex.Message == "outside_workspace" || ex.Message == "protected_path")
        {
            throw new UnauthorizedAccessException("workspace_changed", ex);
        }

        ResolvedPath reResolved;
        try
        {
            reResolved = _resolver.Resolve(resolved.InputPath);
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

        var beforeHash = FileHashService.GetCurrentHash(reResolved.CanonicalPath);
        if (!string.Equals(beforeHash, expected, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        var newBytes = EncodeUtf8Strict(content);
        if (newBytes.Length > MaxInputBytes)
        {
            throw new ArgumentException("content_too_large");
        }

        var beforeText = FileHashService.IsMissing(beforeHash) ? string.Empty : ReadAllTextForDiff(reResolved.CanonicalPath);
        var afterHash = FileHashService.ComputeBytesHash(newBytes);
        string diff;
        try
        {
            AtomicFileReplacement.Replace(reResolved.CanonicalPath, newBytes);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException("access_denied", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new IOException("io_error", ex);
        }

        var verifiedAfterHash = FileHashService.GetCurrentHash(reResolved.CanonicalPath);
        if (!string.Equals(verifiedAfterHash, afterHash, StringComparison.Ordinal))
        {
            throw new IOException("io_error");
        }

        diff = BuildDiffSummary(FileHashService.IsMissing(beforeHash), beforeHash, afterHash, beforeText, content);

        return new JsonObject
        {
            ["path"] = ToRelative(reResolved.CanonicalPath),
            ["before_sha256"] = beforeHash,
            ["after_sha256"] = afterHash,
            ["bytes"] = newBytes.Length,
            ["diff_summary"] = diff,
        };
    }

    private (string pathInput, string content, string expected) ValidateAndResolve(JsonObject arguments, out ResolvedPath resolved, out string expected, out string content)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var pathInput = GetRequiredString(arguments, "path");
        var contentValue = GetRequiredString(arguments, "content");
        var expectedValue = GetRequiredString(arguments, "expected_sha256");

        if (pathInput.Length > FileToolLimits.MaxPathLength)
        {
            throw new ArgumentException("path_too_long");
        }

        FileHashService.ValidateExpectedSha256(expectedValue);

        var newBytesLength = Encoding.UTF8.GetByteCount(contentValue);
        if (newBytesLength > MaxInputBytes)
        {
            throw new ArgumentException("content_too_large");
        }

        ValidateUtf8(contentValue);

        ResolvedPath r;
        try
        {
            r = _resolver.Resolve(pathInput);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("invalid_path", ex);
        }

        if (r.IsProtected)
        {
            throw new UnauthorizedAccessException("protected_path");
        }

        if (r.IsOutsideWorkspace)
        {
            throw new UnauthorizedAccessException("outside_workspace");
        }

        if (Directory.Exists(r.CanonicalPath))
        {
            throw new IOException("is_directory");
        }

        resolved = r;
        expected = expectedValue;
        content = contentValue;
        return (pathInput, contentValue, expectedValue);
    }

    private static void ValidateUtf8(string content)
    {
        try
        {
            var enc = new UTF8Encoding(false, throwOnInvalidBytes: true);
            enc.GetBytes(content);
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("invalid_utf8", ex);
        }

        foreach (var rune in content.EnumerateRunes())
        {
            if (!Rune.IsValid(rune.Value))
            {
                throw new ArgumentException("invalid_utf8");
            }
        }
    }

    private static byte[] EncodeUtf8Strict(string content)
    {
        var enc = new UTF8Encoding(false, throwOnInvalidBytes: true);
        return enc.GetBytes(content);
    }

    private static string ReadAllTextForDiff(string canonicalPath)
    {
        try
        {
            return File.ReadAllText(canonicalPath, Encoding.UTF8);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildDiffSummary(bool wasMissing, string beforeHash, string afterHash, string oldContent, string newContent)
    {
        var sb = new StringBuilder();
        sb.Append("before:").Append(beforeHash).Append(" after:").Append(afterHash).Append(' ');
        if (wasMissing)
        {
            sb.Append("(created) ");
        }

        var oldLines = oldContent.Split('\n');
        var newLines = newContent.Split('\n');
        int oldCount = oldLines.Length;
        int newCount = newLines.Length;
        sb.Append($"lines {oldCount}->{newCount} ");
        int previewLen = Math.Min(200, newContent.Length);
        var preview = newContent.Substring(0, previewLen).Replace("\r", "\\r").Replace("\n", "\\n");
        sb.Append(preview);
        var result = sb.ToString();
        if (result.Length > MaxDiffChars)
        {
            result = result.Substring(0, MaxDiffChars);
        }

        return result;
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
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s is not null)
        {
            return s;
        }

        throw new ArgumentException($"missing_{key}");
    }
}
