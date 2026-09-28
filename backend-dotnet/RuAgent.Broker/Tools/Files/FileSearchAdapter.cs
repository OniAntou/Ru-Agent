using System.Collections.Concurrent;
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

    public FileSearchAdapter(string workspaceRoot, int maxSearchConcurrency = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot);
        _maxSearchConcurrency = Math.Clamp(maxSearchConcurrency, 1, MaxSearchConcurrency);
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
        var fileGlobRegex = fileGlob is null ? null : GlobToRegex(fileGlob);
        var baseResolved = ResolveChecked(basePath);
        var stopwatch = Stopwatch.StartNew();
        var candidates = new List<(string Path, string Relative)>();
        long candidateBytes = 0;
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
                || fileGlobRegex.IsMatch(relative)
                || fileGlobRegex.IsMatch(Path.GetFileName(relative)))
            {
                try
                {
                    HardLinkGuard.RequireSingleLink(baseResolved.CanonicalPath);
                    var info = new FileInfo(baseResolved.CanonicalPath);
                    if (info.Exists && info.Length <= FileToolLimits.MaxSearchFileSize)
                    {
                        candidates.Add((baseResolved.CanonicalPath, relative));
                        candidateBytes += info.Length;
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
        while (stack.Count > 0 && candidates.Count < FileToolLimits.MaxEntries * 32)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (stopwatch.ElapsedMilliseconds > FileToolLimits.MaxTimeMs) { traversalCapped = true; break; }
            var current = stack.Pop();
            if (!visited.Add(current)) continue;
            if (visited.Count > FileToolLimits.MaxEntries * 16) { traversalCapped = true; break; }
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(current); }
            catch (UnauthorizedAccessException) { continue; }
            catch (IOException) { continue; }
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (stopwatch.ElapsedMilliseconds > FileToolLimits.MaxTimeMs) { traversalCapped = true; break; }
                ResolvedPath resolved;
                try { resolved = _resolver.Resolve(entry); } catch { continue; }
                if (resolved.IsProtected || resolved.IsOutsideWorkspace) continue;
                if (Directory.Exists(resolved.CanonicalPath)) { stack.Push(resolved.CanonicalPath); continue; }
                var relative = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, resolved.CanonicalPath);
                if (fileGlobRegex is not null && !fileGlobRegex.IsMatch(relative) && !fileGlobRegex.IsMatch(Path.GetFileName(relative))) continue;
                try
                {
                    HardLinkGuard.RequireSingleLink(resolved.CanonicalPath);
                    var info = new FileInfo(resolved.CanonicalPath);
                    if (!info.Exists || info.Length > FileToolLimits.MaxSearchFileSize) continue;
                    if (candidateBytes + info.Length > FileToolLimits.MaxTotalSearchBytes)
                    {
                        traversalCapped = true;
                        break;
                    }
                    candidateBytes += info.Length;
                }
                catch { continue; }
                candidates.Add((resolved.CanonicalPath, relative));
                if (candidates.Count >= FileToolLimits.MaxEntries * 32) { traversalCapped = true; break; }
            }
        }
        }

        var outcomes = new ConcurrentBag<FileSearchOutcome>();
        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { MaxDegreeOfParallelism = _maxSearchConcurrency, CancellationToken = cancellationToken },
            async (candidate, token) => outcomes.Add(await SearchCandidateAsync(candidate.Path, candidate.Relative, query, queryRegex, caseSensitive, maxResults, token).ConfigureAwait(false))).ConfigureAwait(false);

        var results = outcomes.SelectMany(outcome => outcome.Matches).OrderBy(result => result["path"]!.GetValue<string>(), StringComparer.Ordinal).ThenBy(result => result["line"]!.GetValue<int>()).ToList();
        var final = results.Take(maxResults).ToList();
        var truncated = traversalCapped || results.Count >= maxResults || candidates.Count >= FileToolLimits.MaxEntries * 32 || stopwatch.ElapsedMilliseconds > FileToolLimits.MaxTimeMs;
        stopwatch.Stop();
        var arr = new JsonArray(final.Select(result => (JsonNode)result).ToArray());
        return new JsonObject { ["results"] = arr, ["count"] = final.Count, ["truncated"] = truncated, ["scanned_files"] = candidates.Count, ["scanned_directories"] = visited.Count, ["skipped"] = outcomes.Count(outcome => outcome.Skipped), ["duration_ms"] = (int)stopwatch.ElapsedMilliseconds };
    }

    private async Task<FileSearchOutcome> SearchCandidateAsync(string path, string relative, string query, Regex? queryRegex, bool caseSensitive, int maxResults, CancellationToken cancellationToken)
    {
        try
        {
            HardLinkGuard.RequireSingleLink(path);
            var text = await Task.Run(() =>
            {
                using var stream = HardLinkGuard.OpenRead(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                return ReadSearchText(stream, FileToolLimits.MaxSearchFileSize, 8000, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
            if (text is null) return FileSearchOutcome.SkippedFile;
            var matches = new List<JsonObject>();
            using var reader = new StringReader(text);
            string? line;
            var lineNumber = 0;
            while ((line = reader.ReadLine()) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lineNumber++;
                var match = queryRegex is null ? null : queryRegex.Match(line);
                var index = queryRegex is null ? line.IndexOf(query, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) : (match!.Success ? match.Index : -1);
                if (index >= 0 && matches.Count < maxResults)
                {
                    var preview = line.Length > 512 ? line[..512] : line;
                    matches.Add(new JsonObject { ["path"] = relative, ["line"] = lineNumber, ["column"] = index + 1, ["preview"] = preview, ["line_text"] = preview });
                }
            }
            return new FileSearchOutcome(matches, false);
        }
        catch (OperationCanceledException) { throw; }
        catch (UnauthorizedAccessException) { return FileSearchOutcome.SkippedFile; }
        catch (IOException) { return FileSearchOutcome.SkippedFile; }
        catch (ArgumentException) { return FileSearchOutcome.SkippedFile; }
    }

    private sealed record FileSearchOutcome(IReadOnlyList<JsonObject> Matches, bool Skipped)
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
        var output = new MemoryStream(Math.Min(maximumBytes, 81920));
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
            return StrictUtf8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }
        catch (DecoderFallbackException ex)
        {
            throw new ArgumentException("invalid_utf8", ex);
        }
    }

    private static Regex GlobToRegex(string pattern)
    {
        var normalized = pattern.Replace('\\', '/');
        var escaped = Regex.Escape(normalized);
        escaped = escaped.Replace(@"\*\*", "___DOUBLESTAR___");
        escaped = escaped.Replace(@"\*", "[^/]*");
        escaped = escaped.Replace(@"\?", "[^/]");
        escaped = escaped.Replace("___DOUBLESTAR___", ".*");
        var regexPattern = "^" + escaped + "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
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
