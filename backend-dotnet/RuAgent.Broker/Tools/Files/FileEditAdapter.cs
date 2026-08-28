using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileEditAdapter
{
    public const int MaxInputBytes = 4 * 1024 * 1024;
    public const int MaxFileBytes = 4 * 1024 * 1024;
    public const int MaxDiffChars = 8 * 1024;

    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;

    public FileEditAdapter(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot);
    }

    public JsonObject Prepare(JsonObject arguments)
    {
        var ctx = ValidateAndResolve(arguments, out var resolved, out var expected, out var oldString, out var newString, out var expectedOccurrences);
        var beforeHash = FileHashService.GetCurrentHash(resolved.CanonicalPath);
        if (!string.Equals(beforeHash, expected, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("workspace_changed");
        }

        var text = ReadBoundedText(resolved.CanonicalPath);
        var count = CountOccurrences(text, oldString);
        if (count == 0)
        {
            throw new InvalidOperationException("edit_no_match");
        }

        if (count != expectedOccurrences)
        {
            throw new InvalidOperationException("edit_not_unique");
        }

        var updated = text.Replace(oldString, newString ?? string.Empty, StringComparison.Ordinal);
        ValidateUtf8(updated);
        var updatedBytes = Encoding.UTF8.GetByteCount(updated);
        if (updatedBytes > MaxInputBytes)
        {
            throw new ArgumentException("content_too_large");
        }

        var afterHash = FileHashService.ComputeStringHash(updated);
        var diff = BuildDiffSummary(beforeHash, afterHash, text, updated, oldString, newString);

        return new JsonObject
        {
            ["path"] = ToRelative(resolved.CanonicalPath),
            ["before_sha256"] = beforeHash,
            ["after_sha256"] = afterHash,
            ["replacements"] = count,
            ["diff_summary"] = diff,
        };
    }

    public JsonObject Execute(JsonObject arguments)
    {
        ResolvedPath resolved;
        string expected;
        string oldString;
        string? newString;
        int expectedOccurrences;
        try
        {
            ValidateAndResolve(arguments, out resolved, out expected, out oldString, out newString, out expectedOccurrences);
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

        var text = ReadBoundedText(reResolved.CanonicalPath);
        var count = CountOccurrences(text, oldString);
        if (count == 0)
        {
            throw new InvalidOperationException("edit_no_match");
        }

        if (count != expectedOccurrences)
        {
            throw new InvalidOperationException("edit_not_unique");
        }

        var updated = text.Replace(oldString, newString ?? string.Empty, StringComparison.Ordinal);
        ValidateUtf8(updated);
        var newBytes = EncodeUtf8Strict(updated);
        if (newBytes.Length > MaxInputBytes)
        {
            throw new ArgumentException("content_too_large");
        }

        var afterHash = FileHashService.ComputeBytesHash(newBytes);
        try
        {
            FileMutationGuard.ReplaceIfHashMatches(reResolved.CanonicalPath, newBytes, beforeHash);
        }
        catch (UnauthorizedAccessException ex) when (ex.Message == "workspace_changed")
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException("access_denied", ex);
        }

        var diff = BuildDiffSummary(beforeHash, afterHash, text, updated, oldString, newString);

        return new JsonObject
        {
            ["path"] = ToRelative(reResolved.CanonicalPath),
            ["before_sha256"] = beforeHash,
            ["after_sha256"] = afterHash,
            ["replacements"] = count,
            ["diff_summary"] = diff,
        };
    }

    private (string pathInput, string oldStr, string? newStr, string expected, int occurrences) ValidateAndResolve(
        JsonObject arguments,
        out ResolvedPath resolved,
        out string expected,
        out string oldString,
        out string? newString,
        out int expectedOccurrences)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var pathInput = GetRequiredString(arguments, "path");
        var oldStr = GetRequiredString(arguments, "old_string");
        var expectedValue = GetRequiredString(arguments, "expected_sha256");
        var newStr = GetOptionalString(arguments, "new_string");

        if (string.IsNullOrEmpty(oldStr))
        {
            throw new ArgumentException("old_string_empty");
        }

        if (pathInput.Length > FileToolLimits.MaxPathLength)
        {
            throw new ArgumentException("path_too_long");
        }

        FileHashService.ValidateExpectedSha256(expectedValue);

        if (oldStr.Length > MaxInputBytes || (newStr is not null && newStr.Length > MaxInputBytes))
        {
            throw new ArgumentException("content_too_large");
        }

        ValidateUtf8(oldStr);
        if (newStr is not null)
        {
            ValidateUtf8(newStr);
        }

        expectedOccurrences = 1;
        if (arguments.TryGetPropertyValue("expected_occurrences", out var occNode) && occNode is not null)
        {
            if (occNode is JsonValue v && v.TryGetValue<int>(out var occ))
            {
                if (occ < 1 || occ > 1024)
                {
                    throw new ArgumentException("invalid_expected_occurrences");
                }

                expectedOccurrences = occ;
            }
            else
            {
                throw new ArgumentException("invalid_expected_occurrences");
            }
        }

        if (Encoding.UTF8.GetByteCount(oldStr) > MaxInputBytes)
        {
            throw new ArgumentException("content_too_large");
        }

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

        if (!File.Exists(r.CanonicalPath))
        {
            throw new FileNotFoundException("file_not_found");
        }

        if (Directory.Exists(r.CanonicalPath))
        {
            throw new IOException("is_directory");
        }

        var info = new FileInfo(r.CanonicalPath);
        if (info.Length > MaxFileBytes)
        {
            throw new ArgumentException("file_too_large");
        }

        resolved = r;
        expected = expectedValue;
        oldString = oldStr;
        newString = newStr;
        return (pathInput, oldStr, newStr, expectedValue, expectedOccurrences);
    }

    private static string ReadBoundedText(string canonicalPath)
    {
        var bytes = ReadBoundedBytes(canonicalPath, MaxFileBytes);

        try
        {
            var enc = new UTF8Encoding(false, throwOnInvalidBytes: true);
            return enc.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ArgumentException("invalid_utf8", ex);
        }
    }

    private static byte[] ReadBoundedBytes(string canonicalPath, int maxBytes)
    {
        using var stream = new FileStream(
            canonicalPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.SequentialScan);
        using var output = new MemoryStream(Math.Min(maxBytes, 81920));
        var buffer = new byte[Math.Min(maxBytes, 81920)];
        while (true)
        {
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            if (output.Length > maxBytes - read)
            {
                throw new ArgumentException("file_too_large");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
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

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(value, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += value.Length;
        }

        return count;
    }

    private static string BuildDiffSummary(string beforeHash, string afterHash, string oldText, string newText, string oldStr, string? newStr)
    {
        var sb = new StringBuilder();
        sb.Append("before:").Append(beforeHash).Append(" after:").Append(afterHash).Append(' ');
        sb.Append($"replace \"{Trunc(oldStr, 40)}\" -> \"{Trunc(newStr ?? string.Empty, 40)}\" ");
        var preview = newText.Length > 200 ? newText.Substring(0, 200) : newText;
        sb.Append(preview.Replace("\r", "\\r").Replace("\n", "\\n"));
        var result = sb.ToString();
        if (result.Length > MaxDiffChars)
        {
            result = result.Substring(0, MaxDiffChars);
        }

        return result;
    }

    private static string Trunc(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "...";
    }

    private string ToRelative(string canonicalPath)
    {
        var relative = Path.GetRelativePath(_resolver.CanonicalRoot, canonicalPath);
        if (relative == ".") return ".";
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

    private static string? GetOptionalString(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && s is not null)
        {
            // new_string may be empty string to delete, so allow empty but not null?
            return s;
        }

        if (obj.ContainsKey(key) && obj[key] is null)
        {
            return null;
        }

        // Check if key exists with empty string value? JsonValue with empty string is still s = ""
        // Our earlier check returns s even if empty. That's okay.
        // If key missing, return null (means no new_string -> treat as deletion?)
        // But for edit, new_string can be missing -> treat as empty.
        if (!obj.ContainsKey(key))
        {
            return string.Empty;
        }

        // If key is present but not string (e.g., null node)
        return null;
    }
}
