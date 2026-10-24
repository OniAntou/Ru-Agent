using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileSearchAdapter
{
    public const int MaxSearchConcurrency = 8;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;
    private readonly int _maxSearchConcurrency;

    public FileSearchAdapter(string workspaceRoot, int maxSearchConcurrency = 4, bool allowProtectedPaths = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot, allowProtectedPaths);
        _maxSearchConcurrency = Math.Clamp(maxSearchConcurrency, 1, MaxSearchConcurrency);
    }

    private static readonly HashSet<string> AlwaysGeneratedDirectories =
        new(["node_modules", "__pycache__", ".git"], StringComparer.OrdinalIgnoreCase);
    private static readonly string[] ProjectFilePatterns = ["*.csproj", "*.fsproj", "*.vbproj", "package.json"];

    /// <summary>
    /// Skips VCS and dependency/build-output directories during recursive traversal.
    /// `bin`/`obj` are pruned only beside a project file so a source folder of that
    /// name is still searched; naming such a directory as the search path searches it.
    /// </summary>
    private static bool IsGeneratedDirectory(string parent, string directory, Dictionary<string, bool> projectDirectories)
    {
        var name = Path.GetFileName(directory);
        if (AlwaysGeneratedDirectories.Contains(name)) return true;
        if (!name.Equals("bin", StringComparison.OrdinalIgnoreCase) && !name.Equals("obj", StringComparison.OrdinalIgnoreCase)) return false;
        if (projectDirectories.TryGetValue(parent, out var hasProject)) return hasProject;
        try { hasProject = ProjectFilePatterns.Any(pattern => Directory.EnumerateFiles(parent, pattern).Any()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { hasProject = false; }
        projectDirectories[parent] = hasProject;
        return hasProject;
    }

    public Task<JsonObject> ExecuteAsync(JsonObject arguments, CancellationToken cancellationToken) =>
        ExecuteConcurrentAsync(arguments, cancellationToken);

    private async Task<JsonObject> ExecuteConcurrentAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var basePath = GetOptionalString(arguments, "path") ?? ".";
        var query = GetRequiredString(arguments, "query");
        if (query.Length == 0 || query.Length > FileToolLimits.MaxQueryLength || query.Contains('\0')) throw new ArgumentException("invalid_query");
        var fileGlob = GetOptionalString(arguments, "file_glob");
        if (fileGlob is not null && fileGlob.Length > FileToolLimits.MaxPatternLength) throw new ArgumentException("pattern_too_long");
        var caseSensitive = arguments["case_sensitive"] is JsonValue cs && cs.TryGetValue<bool>(out var csValue) && csValue;
        var isRegex = arguments["is_regex"] is JsonValue re && re.TryGetValue<bool>(out var reValue) && reValue;
        Regex? queryRegex = null;
        if (isRegex)
        {
            try { queryRegex = new Regex(query, (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException) { throw new ArgumentException("invalid_regex"); }
        }
        var maxResults = arguments["max_results"] is JsonValue max && max.TryGetValue<int>(out var maxValue) ? maxValue : FileToolLimits.MaxResults;
        if (maxResults < 1 || maxResults > FileToolLimits.MaxResults) throw new ArgumentException("invalid_max_results");
        var offset = arguments["offset"] is JsonValue off && off.TryGetValue<int>(out var offValue) ? offValue : 0;
        if (offset < 0 || offset > FileToolLimits.MaxOffset) throw new ArgumentException("invalid_offset");
        // One extra match distinguishes a full final page from a known next page.
        // Nonzero offsets are skipped in-order below, without retaining their JSON.
        var collectLimit = maxResults + 1;
        var fileGlobRegex = fileGlob is null ? null : new FileGlobPattern(fileGlob);
        var baseResolved = ResolveChecked(basePath);
        var stopwatch = Stopwatch.StartNew();
        var candidates = new List<(string Path, string Relative)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var traversalCapped = false;
        if (!Directory.Exists(baseResolved.CanonicalPath))
        {
            // A file path searches just that file through the same guards
            // (protected/outside already rejected above; hard-link and size
            // below). Anything else is still not_a_directory.
            if (!File.Exists(baseResolved.CanonicalPath))
            {
                throw new IOException("not_a_directory");
            }

            var relative = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, baseResolved.CanonicalPath);
            if (fileGlobRegex is null
                || fileGlobRegex.IsMatch(relative))
            {
                try
                {
                    HardLinkGuard.RequireSingleLink(baseResolved.CanonicalPath);
                    var info = new FileInfo(baseResolved.CanonicalPath);
                    if (info.Exists && info.Length <= FileToolLimits.MaxSearchFileSize)
                    {
                        candidates.Add((baseResolved.CanonicalPath, relative));
                    }
                }
                catch
                {
                    // Guarded file: search nothing rather than fail the call.
                }
            }
        }
        else
        {
        var stack = new Stack<string>([baseResolved.CanonicalPath]);
        var projectDirectories = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        while (stack.Count > 0 && candidates.Count < FileToolLimits.MaxEntries * 32)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.ElapsedMilliseconds > FileToolLimits.MaxTimeMs) { traversalCapped = true; break; }
            var current = stack.Pop();
            if (!visited.Add(current)) continue;
            if (visited.Count > FileToolLimits.MaxEntries * 16) { traversalCapped = true; break; }
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(current).EnumerateFileSystemInfos(); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var item in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.ElapsedMilliseconds > FileToolLimits.MaxTimeMs) { traversalCapped = true; break; }
                // Enumeration already carries attributes, so a plain file that cannot
                // match the glob is dropped before the per-entry path resolution
                // (handle open + final-path lookup), which dominates build-output trees.
                if (fileGlobRegex is not null
                    && (item.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0
                    && !fileGlobRegex.IsMatch(WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, item.FullName)))
                    continue;
                var entry = item.FullName;
                ResolvedPath resolved;
                try { resolved = _resolver.Resolve(entry); } catch { continue; }
                if (resolved.IsProtected || resolved.IsOutsideWorkspace) continue;
                if (Directory.Exists(resolved.CanonicalPath))
                {
                    if (!IsGeneratedDirectory(current, resolved.CanonicalPath, projectDirectories)) stack.Push(resolved.CanonicalPath);
                    continue;
                }
                var relative = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, resolved.CanonicalPath);
                if (fileGlobRegex is not null && !fileGlobRegex.IsMatch(relative)) continue;
                try
                {
                    HardLinkGuard.RequireSingleLink(resolved.CanonicalPath);
                    var info = new FileInfo(resolved.CanonicalPath);
                    if (!info.Exists || info.Length > FileToolLimits.MaxSearchFileSize) continue;
                }
                catch { continue; }
                candidates.Add((resolved.CanonicalPath, relative));
                if (candidates.Count >= FileToolLimits.MaxEntries * 32) { traversalCapped = true; break; }
            }
        }
        }

        // Nonzero offsets scan candidates sequentially so skipped matches never
        // become retained JSON and parallel files cannot multiply page allocation.
        candidates.Sort((left, right) => StringComparer.Ordinal.Compare(left.Relative, right.Relative));
        var scannedFiles = 0;
        var skippedFiles = 0;
        var searchIncomplete = false;
        var results = new List<JsonObject>();
        if (offset > 0)
        {
            var remainingSkip = offset;
            foreach (var candidate in candidates)
            {
                if (results.Count >= collectLimit) break;
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.ElapsedMilliseconds > FileToolLimits.MaxTimeMs) { traversalCapped = true; break; }
                var outcome = await SearchCandidateAsync(candidate.Path, candidate.Relative, query,
                    queryRegex, caseSensitive, collectLimit - results.Count, remainingSkip, stopwatch, cancellationToken).ConfigureAwait(false);
                scannedFiles++;
                if (outcome.Skipped) skippedFiles++;
                searchIncomplete |= outcome.Incomplete;
                remainingSkip -= outcome.SkippedMatches;
                results.AddRange(outcome.Matches);
            }
        }
        else
        {
            // Keep the common first-page path parallel while bounding each batch
            // to the page and one lookahead, independent of the offset ceiling.
            for (var index = 0; index < candidates.Count && results.Count < collectLimit; index += _maxSearchConcurrency)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.ElapsedMilliseconds > FileToolLimits.MaxTimeMs) { traversalCapped = true; break; }
                var batchSize = Math.Min(_maxSearchConcurrency, candidates.Count - index);
                var batch = new Task<FileSearchOutcome>[batchSize];
                for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
                {
                    var candidate = candidates[index + batchIndex];
                    batch[batchIndex] = SearchCandidateAsync(candidate.Path, candidate.Relative, query,
                        queryRegex, caseSensitive, collectLimit, 0, stopwatch, cancellationToken);
                }
                var completed = await Task.WhenAll(batch).ConfigureAwait(false);
                scannedFiles += completed.Length;
                foreach (var outcome in completed)
                {
                    if (outcome.Skipped) skippedFiles++;
                    searchIncomplete |= outcome.Incomplete;
                    results.AddRange(outcome.Matches.Take(collectLimit - results.Count));
                }
            }
        }

        var final = results.Take(maxResults).ToList();
        var hasMore = results.Count > maxResults;
        var incomplete = traversalCapped || searchIncomplete;
        int? nextOffset = hasMore && offset + final.Count <= FileToolLimits.MaxOffset ? offset + final.Count : null;
        stopwatch.Stop();
        return new JsonObject
        {
            ["results"] = new JsonArray(final.Select(result => (JsonNode)result).ToArray()),
            ["count"] = final.Count,
            ["truncated"] = incomplete || hasMore,
            ["offset"] = offset,
            ["next_offset"] = nextOffset,
            ["scan_incomplete"] = incomplete,
            ["discovered_files"] = candidates.Count,
            ["scanned_files"] = scannedFiles,
            ["scanned_directories"] = visited.Count,
            ["skipped"] = skippedFiles,
            ["duration_ms"] = (int)stopwatch.ElapsedMilliseconds,
        };
    }

    private async Task<FileSearchOutcome> SearchCandidateAsync(string path, string relative, string query, Regex? queryRegex, bool caseSensitive, int collectLimit, int skipMatches, Stopwatch budget, CancellationToken cancellationToken)
    {
        try
        {
            HardLinkGuard.RequireSingleLink(path);
            var text = await Task.Run(() =>
            {
                using var stream = HardLinkGuard.OpenRead(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                HardLinkGuard.RequireFinalPathMatches(stream.SafeFileHandle, path);
                return ReadSearchText(stream, FileToolLimits.MaxSearchFileSize, 8000, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
            if (text is null) return FileSearchOutcome.SkippedFile;
            var matches = new List<JsonObject>();
            using var reader = new StringReader(text);
            string? line;
            var lineNumber = 0;
            var incomplete = false;
            var skippedMatches = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (budget.ElapsedMilliseconds > FileToolLimits.MaxTimeMs) { incomplete = true; break; }
                lineNumber++;
                Match? match = null;
                if (queryRegex is not null)
                {
                    try
                    {
                        match = queryRegex.Match(line);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        // Catastrophic backtracking on one line must not fail the
                        // whole search, but the caller must know the scan is incomplete.
                        incomplete = true;
                        continue;
                    }
                }

                var index = queryRegex is null ? line.IndexOf(query, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) : (match!.Success ? match.Index : -1);
                if (index >= 0)
                {
                    if (skippedMatches < skipMatches)
                    {
                        skippedMatches++;
                        continue;
                    }
                    if (matches.Count >= collectLimit) break;
                    var previewStart = line.Length > 512 ? Math.Max(0, index - 128) : 0;
                    var preview = line.Substring(previewStart, Math.Min(512, line.Length - previewStart));
                    matches.Add(new JsonObject { ["path"] = relative, ["line"] = lineNumber, ["column"] = index + 1,
                        ["preview_column"] = previewStart + 1, ["preview"] = preview, ["line_text"] = preview });
                    if (matches.Count == collectLimit) break;
                }
            }
            return new FileSearchOutcome(matches, false, incomplete, skippedMatches);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return FileSearchOutcome.SkippedFile; }
        catch (IOException) { return FileSearchOutcome.SkippedFile; }
        catch (ArgumentException) { return FileSearchOutcome.SkippedFile; }
    }

    private sealed record FileSearchOutcome(IReadOnlyList<JsonObject> Matches, bool Skipped, bool Incomplete = false, int SkippedMatches = 0)
    {
        public static FileSearchOutcome SkippedFile => new(Array.Empty<JsonObject>(), true);
    }

    private ResolvedPath ResolveChecked(string input)
    {
        try
        {
            var resolved = _resolver.Resolve(input);
            if (resolved.IsProtected)
            {
                throw new UnauthorizedAccessException("protected_path");
            }

            if (resolved.IsOutsideWorkspace)
            {
                throw new UnauthorizedAccessException("outside_workspace");
            }

            return resolved;
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException("invalid_path", ex);
        }
    }

    internal static string? ReadSearchText(Stream stream, int maximumBytes, int binaryProbeBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (binaryProbeBytes < 0) throw new ArgumentOutOfRangeException(nameof(binaryProbeBytes));
        // Probe for binary (zero byte) while retaining prefix
        var probeLength = Math.Min(binaryProbeBytes, maximumBytes);
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[Math.Min(maximumBytes, 81920)];
        var totalRead = 0;
        var probeChecked = 0;

        while (totalRead < maximumBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var toRead = Math.Min(buffer.Length, maximumBytes - totalRead);
            if (toRead == 0) break;
            var read = stream.Read(buffer, 0, toRead);
            if (read == 0) break;

            // Check binary within probe window
            var checkUpTo = Math.Min(read, Math.Max(0, probeLength - probeChecked));
            for (int i = 0; i < checkUpTo; i++)
            {
                if (buffer[i] == 0) return null;
            }
            probeChecked += read;

            if (output.Length > maximumBytes - read)
            {
                throw new ArgumentException("file_too_large");
            }
            output.Write(buffer, 0, read);
            totalRead += read;

            // If we have already read beyond probe and the stream still has more, continue; the loop will detect overflow on next iteration
            // Also check if stream has more data beyond maximumBytes by attempting one more byte
            if (totalRead >= maximumBytes)
            {
                // Peek one more byte to detect file_too_large due to concurrent growth
                var peek = stream.ReadByte();
                if (peek != -1)
                {
                    throw new ArgumentException("file_too_large");
                }
                break;
            }
        }

        // If totalRead == 0, empty file -> return empty string (not binary)
        try
        {
            var bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
            // A UTF-8 signature is encoding metadata, not a column on line one.
            if (bytes.StartsWith("\uFEFF"u8)) bytes = bytes[3..];
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new ArgumentException("invalid_utf8", ex);
        }
    }

    private static string GetRequiredString(JsonObject obj, string key)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
        {
            return s;
        }

        throw new ArgumentException($"missing_{key}");
    }

    private static string? GetOptionalString(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
