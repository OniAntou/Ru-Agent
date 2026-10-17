using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FilePatchAdapter
{
    public const int MaxPatchBytes = UnifiedPatchParser.MaxPatchBytes;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;
    private readonly Func<string, bool>? _simulateFailure;

    public FilePatchAdapter(string workspaceRoot, Func<string, bool>? simulateFailure = null, bool allowProtectedPaths = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot, allowProtectedPaths);
        _simulateFailure = simulateFailure;
    }

    public JsonObject Prepare(JsonObject arguments)
    {
        var (patchText, expectedMap) = ValidateArguments(arguments);
        var parsed = UnifiedPatchParser.Parse(patchText);
        ValidateExpectedMap(parsed, expectedMap);
        var resolvedMap = ResolveAndValidateTargets(parsed, expectedMap);
        ValidatePreimages(resolvedMap, expectedMap);

        var resultArray = new JsonArray();
        foreach (var entry in resolvedMap)
        {
            var currentHash = FileHashService.GetCurrentHash(entry.CanonicalPath);
            string oldContent = string.Empty;
            if (!FileHashService.IsMissing(currentHash))
            {
                oldContent = ReadText(entry.CanonicalPath);
            }

            var newContent = entry.IsDeleted ? string.Empty : UnifiedPatchParser.ApplyHunks(oldContent, entry.Parsed.Hunks);
            if (!entry.IsDeleted)
            {
                ValidateUtf8AndSize(newContent);
            }

            var afterHash = entry.IsDeleted ? FileHashService.Missing : FileHashService.ComputeStringHash(newContent);
            var diff = BuildDiffSummary(currentHash, afterHash, oldContent, newContent);
            resultArray.Add(new JsonObject
            {
                ["path"] = entry.RelativePath,
                ["before_sha256"] = currentHash,
                ["after_sha256"] = afterHash,
                ["diff_summary"] = diff,
            });
        }

        return new JsonObject
        {
            ["files"] = resultArray,
            ["count"] = resultArray.Count,
        };
    }

    public JsonObject Execute(JsonObject arguments)
    {
        var (patchText, expectedMap) = ValidateArguments(arguments);
        var parsed = UnifiedPatchParser.Parse(patchText);
        ValidateExpectedMap(parsed, expectedMap);
        List<ResolvedPatchEntry> resolvedMap;
        try
        {
            resolvedMap = ResolveAndValidateTargets(parsed, expectedMap);
        }
        catch (UnauthorizedAccessException ex) when (ex.Message == "outside_workspace" || ex.Message == "protected_path")
        {
            throw new UnauthorizedAccessException("workspace_changed", ex);
        }

        try
        {
            ValidatePreimages(resolvedMap, expectedMap);
        }
        catch (UnauthorizedAccessException ex) when (ex.Message == "workspace_changed")
        {
            throw;
        }

        var journal = new FileMutationJournal();
        var results = new List<JsonObject>();
        try
        {
            foreach (var entry in resolvedMap)
            {
                ResolvedPath reResolved;
                try
                {
                    reResolved = _resolver.Resolve(entry.Parsed.TargetPath);
                }
                catch (ArgumentException ex)
                {
                    throw new UnauthorizedAccessException("workspace_changed", ex);
                }

                if (!string.Equals(reResolved.CanonicalPath, entry.CanonicalPath, StringComparison.OrdinalIgnoreCase)
                    || reResolved.Classification != entry.Classification)
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                if (reResolved.IsProtected || reResolved.IsOutsideWorkspace)
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                var currentHash = FileHashService.GetCurrentHash(reResolved.CanonicalPath);
                var expectedHash = expectedMap[entry.Parsed.TargetPath];
                if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
                {
                    throw new UnauthorizedAccessException("workspace_changed");
                }

                string oldContent = string.Empty;
                byte[]? beforeBytes = null;
                bool wasMissing = FileHashService.IsMissing(currentHash);
                if (!wasMissing)
                {
                    beforeBytes = ReadBoundedBytes(
                        reResolved.CanonicalPath,
                        FileWriteAdapter.MaxInputBytes,
                        "file_too_large");
                    try
                    {
                        oldContent = StrictUtf8.GetString(beforeBytes);
                    }
                    catch (DecoderFallbackException ex)
                    {
                        throw new ArgumentException("invalid_utf8", ex);
                    }
                }

                string newContent;
                if (entry.IsDeleted)
                {
                    newContent = string.Empty;
                }
                else
                {
                    newContent = UnifiedPatchParser.ApplyHunks(oldContent, entry.Parsed.Hunks);
                    ValidateUtf8AndSize(newContent);
                }

                var beforeHash = currentHash;
                var afterHash = entry.IsDeleted ? FileHashService.Missing : FileHashService.ComputeStringHash(newContent);

                journal.Record(reResolved.CanonicalPath, beforeHash, beforeBytes, wasMissing);

                if (_simulateFailure != null && _simulateFailure(reResolved.CanonicalPath))
                {
                    throw new IOException("simulated_replacement_failure");
                }

                if (entry.IsDeleted)
                {
                    try
                    {
                        FileMutationGuard.DeleteIfHashMatches(reResolved.CanonicalPath, beforeHash);
                    }
                    catch (UnauthorizedAccessException ex) when (ex.Message != "workspace_changed")
                    {
                        throw new UnauthorizedAccessException("access_denied", ex);
                    }
                }
                else
                {
                    var newBytes = Encoding.UTF8.GetBytes(newContent);
                    try
                    {
                        FileMutationGuard.ReplaceIfHashMatches(reResolved.CanonicalPath, newBytes, beforeHash);
                    }
                    catch (UnauthorizedAccessException ex) when (ex.Message != "workspace_changed")
                    {
                        throw new UnauthorizedAccessException("access_denied", ex);
                    }
                }

                journal.MarkApplied(afterHash);
                var verifiedAfter = FileHashService.GetCurrentHash(reResolved.CanonicalPath);
                if (!string.Equals(verifiedAfter, afterHash, StringComparison.Ordinal))
                {
                    throw new IOException("io_error");
                }

                var diff = BuildDiffSummary(beforeHash, afterHash, oldContent, newContent);
                results.Add(new JsonObject
                {
                    ["path"] = entry.RelativePath,
                    ["before_sha256"] = beforeHash,
                    ["after_sha256"] = afterHash,
                    ["diff_summary"] = diff,
                });
            }

            journal.Commit();
        }
        catch
        {
            journal.Rollback();
            throw;
        }
        finally
        {
            journal.Dispose();
        }

        var arr = new JsonArray();
        foreach (var r in results)
        {
            arr.Add(r.DeepClone());
        }

        return new JsonObject
        {
            ["files"] = arr,
            ["count"] = arr.Count,
        };
    }

    private (string patchText, Dictionary<string, string> expectedMap) ValidateArguments(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (!arguments.TryGetPropertyValue("patch", out var patchNode) || patchNode is not JsonValue patchVal || !patchVal.TryGetValue<string>(out var patchText) || patchText is null)
        {
            throw new ArgumentException("missing_patch");
        }

        if (string.IsNullOrWhiteSpace(patchText))
        {
            throw new ArgumentException("patch_empty");
        }

        if (Encoding.UTF8.GetByteCount(patchText) > MaxPatchBytes)
        {
            throw new ArgumentException("patch_too_large");
        }

        if (!arguments.TryGetPropertyValue("expected_hashes", out var hashesNode) || hashesNode is not JsonObject hashesObj)
        {
            throw new ArgumentException("missing_expected_hashes");
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in hashesObj)
        {
            var key = kv.Key;
            if (string.IsNullOrWhiteSpace(key) || key.Length > FileToolLimits.MaxPathLength)
            {
                throw new ArgumentException("invalid_expected_hash_path");
            }

            if (kv.Value is not JsonValue hv || !hv.TryGetValue<string>(out var hashStr) || hashStr is null)
            {
                throw new ArgumentException("invalid_expected_hash_value");
            }

            FileHashService.ValidateExpectedSha256(hashStr);
            if (!map.TryAdd(key.Replace('\\', '/'), hashStr))
            {
                throw new ArgumentException("patch_duplicate_path");
            }
        }

        if (map.Count == 0)
        {
            throw new ArgumentException("expected_hashes_empty");
        }

        if (map.Count > UnifiedPatchParser.MaxFiles)
        {
            throw new ArgumentException("patch_too_many_files");
        }

        return (patchText, map);
    }

    private void ValidateExpectedMap(IReadOnlyList<ParsedPatchFile> parsed, Dictionary<string, string> expectedMap)
    {
        var parsedSet = parsed.Select(p => p.TargetPath).ToHashSet(StringComparer.Ordinal);
        if (parsedSet.Count != expectedMap.Count)
        {
            throw new ArgumentException("expected_hash_count_mismatch");
        }

        foreach (var p in parsed)
        {
            if (!expectedMap.ContainsKey(p.TargetPath))
            {
                throw new ArgumentException("missing_expected_hash_for_target");
            }
        }

        foreach (var kv in expectedMap)
        {
            if (!parsedSet.Contains(kv.Key))
            {
                throw new ArgumentException("expected_hash_for_unknown_target");
            }
        }
    }

    private sealed record ResolvedPatchEntry(
        ParsedPatchFile Parsed,
        string CanonicalPath,
        PathClassification Classification,
        string RelativePath,
        ResolvedPath Resolved,
        bool IsDeleted);

    private List<ResolvedPatchEntry> ResolveAndValidateTargets(IReadOnlyList<ParsedPatchFile> parsed, Dictionary<string, string> expectedMap)
    {
        var list = new List<ResolvedPatchEntry>(parsed.Count);
        var canonicalSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in parsed)
        {
            ResolvedPath resolved;
            try
            {
                resolved = _resolver.Resolve(file.TargetPath);
            }
            catch (ArgumentException ex)
            {
                throw new ArgumentException("invalid_patch_path", ex);
            }

            if (resolved.IsProtected)
            {
                throw new UnauthorizedAccessException("protected_path");
            }

            if (resolved.IsOutsideWorkspace)
            {
                throw new UnauthorizedAccessException("outside_workspace");
            }

            var rel = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, resolved.CanonicalPath);
            if (!canonicalSeen.Add(resolved.CanonicalPath))
            {
                throw new ArgumentException("patch_duplicate_path");
            }

            list.Add(new ResolvedPatchEntry(file, resolved.CanonicalPath, resolved.Classification, rel, resolved, file.IsDeletedFile));
        }

        return list;
    }

    private void ValidatePreimages(List<ResolvedPatchEntry> resolvedMap, Dictionary<string, string> expectedMap)
    {
        foreach (var entry in resolvedMap)
        {
            var expected = expectedMap[entry.Parsed.TargetPath];
            var current = FileHashService.GetCurrentHash(entry.CanonicalPath);
            if (!string.Equals(current, expected, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("workspace_changed");
            }
        }
    }

    private static void ValidateUtf8AndSize(string content)
    {
        try
        {
            if (StrictUtf8.GetByteCount(content) > FileWriteAdapter.MaxInputBytes)
            {
                throw new ArgumentException("content_too_large");
            }
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException("invalid_utf8", ex);
        }
    }

    private static string ReadText(string canonicalPath)
    {
        byte[] bytes;
        try
        {
            bytes = ReadBoundedBytes(canonicalPath, FileWriteAdapter.MaxInputBytes, "file_too_large");
        }
        catch (FileNotFoundException ex)
        {
            throw new UnauthorizedAccessException("workspace_changed", ex);
        }
        catch (DirectoryNotFoundException ex)
        {
            throw new UnauthorizedAccessException("workspace_changed", ex);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ArgumentException("invalid_utf8", ex);
        }
    }

    private static byte[] ReadBoundedBytes(string canonicalPath, int maxBytes, string tooLargeCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        if (maxBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        using var stream = HardLinkGuard.OpenRead(
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
                throw new ArgumentException(tooLargeCode);
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static string BuildDiffSummary(string beforeHash, string afterHash, string oldContent, string newContent)
    {
        var sb = new StringBuilder();
        sb.Append("before:").Append(beforeHash).Append(" after:").Append(afterHash).Append(' ');
        var preview = newContent.Length > 200 ? newContent.Substring(0, 200) : newContent;
        sb.Append(preview.Replace("\r", "\\r").Replace("\n", "\\n"));
        var result = sb.ToString();
        if (result.Length > FileWriteAdapter.MaxDiffChars)
        {
            result = result.Substring(0, FileWriteAdapter.MaxDiffChars);
        }

        return result;
    }

}
