using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileSearchAdapter
{
    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;

    public FileSearchAdapter(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot);
    }

    public JsonObject Execute(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var basePath = GetOptionalString(arguments, "path") ?? ".";
        if (string.IsNullOrWhiteSpace(basePath))
        {
            basePath = ".";
        }

        var query = GetRequiredString(arguments, "query");
        if (query.Length == 0 || query.Length > FileToolLimits.MaxQueryLength)
        {
            throw new ArgumentException("invalid_query");
        }

        if (query.Contains('\0'))
        {
            throw new ArgumentException("invalid_query");
        }

        var fileGlob = GetOptionalString(arguments, "file_glob");
        if (fileGlob is not null && fileGlob.Length > FileToolLimits.MaxPatternLength)
        {
            throw new ArgumentException("pattern_too_long");
        }

        var caseSensitive = false;
        if (arguments.TryGetPropertyValue("case_sensitive", out var csNode) && csNode is JsonValue csv && csv.TryGetValue<bool>(out var csVal))
        {
            caseSensitive = csVal;
        }
        else if (arguments.ContainsKey("case_sensitive") && arguments["case_sensitive"] is not null && arguments["case_sensitive"] is not JsonValue)
        {
            throw new ArgumentException("invalid_case_sensitive");
        }

        var maxResults = FileToolLimits.MaxResults;
        if (arguments.TryGetPropertyValue("max_results", out var maxNode) && maxNode is not null)
        {
            if (maxNode is JsonValue mv && mv.TryGetValue<int>(out var v))
            {
                if (v < 1 || v > FileToolLimits.MaxResults)
                {
                    throw new ArgumentException("invalid_max_results");
                }

                maxResults = v;
            }
            else
            {
                throw new ArgumentException("invalid_max_results");
            }
        }

        if (basePath.Length > FileToolLimits.MaxPathLength)
        {
            throw new ArgumentException("path_too_long");
        }

        var baseResolved = ResolveChecked(basePath);
        var baseCanonical = baseResolved.CanonicalPath;
        if (!Directory.Exists(baseCanonical))
        {
            throw new IOException("not_a_directory");
        }

        var sw = Stopwatch.StartNew();
        var results = new List<JsonObject>();
        int skipped = 0;
        int scannedFiles = 0;
        long totalBytes = 0;
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        Regex? fileGlobRegex = null;
        if (fileGlob is not null)
        {
            fileGlobRegex = GlobToRegex(fileGlob);
        }

        var stack = new Stack<string>();
        stack.Push(baseCanonical);

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs)
            {
                break;
            }

            if (totalBytes > FileToolLimits.MaxTotalSearchBytes)
            {
                break;
            }

            if (results.Count >= maxResults && scannedFiles > maxResults * 2)
            {
                // Bounded: we have enough results, can stop early to avoid unbounded accumulation
                // But spec says drain/truncate without unbounded accumulation — we should not accumulate more than maxResults
                // So break
                break;
            }

            var current = stack.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(current);
            }
            catch (UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }
            catch (IOException)
            {
                skipped++;
                continue;
            }

            foreach (var entry in entries)
            {
                if (sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs)
                {
                    break;
                }

                if (totalBytes > FileToolLimits.MaxTotalSearchBytes)
                {
                    break;
                }

                ResolvedPath entryResolved;
                try
                {
                    entryResolved = _resolver.Resolve(entry);
                }
                catch
                {
                    skipped++;
                    continue;
                }

                if (entryResolved.IsProtected || entryResolved.IsOutsideWorkspace)
                {
                    skipped++;
                    continue;
                }

                var relative = GetRelative(entryResolved.CanonicalPath);
                bool isDir = Directory.Exists(entryResolved.CanonicalPath);
                if (isDir)
                {
                    if (scannedFiles > FileToolLimits.MaxEntries * 8)
                    {
                        skipped++;
                        continue;
                    }

                    stack.Push(entryResolved.CanonicalPath);
                    continue;
                }

                // File: check file_glob filter
                if (fileGlobRegex is not null)
                {
                    bool matchesGlob = false;
                    try
                    {
                        matchesGlob = fileGlobRegex.IsMatch(relative) || fileGlobRegex.IsMatch(Path.GetFileName(relative));
                    }
                    catch
                    {
                        matchesGlob = false;
                    }

                    if (!matchesGlob)
                    {
                        continue;
                    }
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(entryResolved.CanonicalPath);
                    if (!info.Exists)
                    {
                        skipped++;
                        continue;
                    }

                    if (info.Length > FileToolLimits.MaxSearchFileSize)
                    {
                        skipped++;
                        continue;
                    }
                }
                catch
                {
                    skipped++;
                    continue;
                }

                if (IsBinaryFile(entryResolved.CanonicalPath))
                {
                    skipped++;
                    continue;
                }

                // Search file content streaming
                try
                {
                    using var stream = new FileStream(entryResolved.CanonicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
                    string? line;
                    int lineNumber = 0;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNumber++;
                        if (sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs)
                        {
                            break;
                        }

                        if (totalBytes > FileToolLimits.MaxTotalSearchBytes)
                        {
                            break;
                        }

                        // Bound line length to avoid unbounded allocation per line
                        if (line.Length > 8192)
                        {
                            // Truncate line for search but still check
                            line = line.Substring(0, 8192);
                        }

                        totalBytes += Encoding.UTF8.GetByteCount(line) + 1;

                        int idx = line.IndexOf(query, comparison);
                        if (idx >= 0)
                        {
                            if (results.Count < maxResults)
                            {
                                var preview = line.Length > 512 ? line.Substring(0, 512) : line;
                                results.Add(new JsonObject
                                {
                                    ["path"] = relative,
                                    ["line"] = lineNumber,
                                    ["column"] = idx + 1,
                                    ["preview"] = preview,
                                    ["line_text"] = preview,
                                });
                            }
                            else
                            {
                                // Already at cap, don't accumulate more, but continue draining file without adding?
                                // To prove drain without unbounded, we still read remainder but don't add
                            }
                        }

                        if (results.Count >= maxResults && lineNumber > 100)
                        {
                            // If we already have maxResults, we could break file early to save time, but need to drain?
                            // For bounded, break file early is okay as long as we don't allocate unbounded
                            // We will break file loop if we have maxResults and have scanned enough
                            if (results.Count >= maxResults)
                            {
                                // Drain remaining lines without adding? For simplicity break
                                break;
                            }
                        }
                    }

                    scannedFiles++;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped++;
                    continue;
                }
                catch (IOException)
                {
                    skipped++;
                    continue;
                }

                if (results.Count >= maxResults && scannedFiles > maxResults)
                {
                    // Could break outer enumeration early
                }

                if (sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs)
                {
                    break;
                }
            }
        }

        // Deterministic ordinal sort by path then line
        var sorted = results.OrderBy(r => r["path"]!.GetValue<string>(), StringComparer.Ordinal)
            .ThenBy(r => r["line"]!.GetValue<int>())
            .ToList();

        bool truncated = false;
        List<JsonObject> final;
        if (sorted.Count > maxResults)
        {
            truncated = true;
            final = sorted.Take(maxResults).ToList();
        }
        else
        {
            final = sorted;
            truncated = sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs || totalBytes > FileToolLimits.MaxTotalSearchBytes || (sorted.Count == maxResults && scannedFiles > 0);
            // If we hit exact maxResults, truncated true to indicate potential more matches
            if (sorted.Count == maxResults && !truncated)
            {
                // Check if we stopped early due to caps — if we have more files to scan, truncated
                // Since we break early when results >= maxResults, we should mark truncated if we didn't fully drain
                // For simplicity, if count == maxResults, mark truncated true unless we fully scanned all files without caps
                // We can check if stack still has entries or we broke early
                // To avoid false positive, only mark truncated if we broke due to maxResults
                // We'll set truncated true if count == maxResults and (sw not timed out and totalBytes not exceeded) but we still have results == maxResults
                // The spec says truncated metadata bounded, so we should be conservative: if count == maxResults, truncated = true
                truncated = true;
            }

            if (final.Count < sorted.Count)
            {
                truncated = true;
            }
        }

        // If we never reached maxResults and not time/byte capped, truncated false
        if (final.Count < maxResults && sw.ElapsedMilliseconds <= FileToolLimits.MaxTimeMs && totalBytes <= FileToolLimits.MaxTotalSearchBytes)
        {
            truncated = false;
        }

        var arr = new JsonArray();
        foreach (var r in final)
        {
            arr.Add(r.DeepClone());
        }

        return new JsonObject
        {
            ["results"] = arr,
            ["count"] = final.Count,
            ["truncated"] = truncated,
            ["scanned_files"] = scannedFiles,
            ["skipped"] = skipped,
            ["duration_ms"] = (int)sw.ElapsedMilliseconds,
        };
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

    private string GetRelative(string canonicalPath)
    {
        var relative = Path.GetRelativePath(_resolver.CanonicalRoot, canonicalPath);
        if (relative == ".")
        {
            return ".";
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
    }

    private static bool IsBinaryFile(string path)
    {
        const int sampleSize = 8000;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var length = (int)Math.Min(sampleSize, stream.Length);
            if (length == 0)
            {
                return false;
            }

            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            for (int i = 0; i < read; i++)
            {
                if (buffer[i] == 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
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
