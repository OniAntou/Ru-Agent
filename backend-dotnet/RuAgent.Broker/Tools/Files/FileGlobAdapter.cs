using System.Diagnostics;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileGlobAdapter
{
    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;

    public FileGlobAdapter(string workspaceRoot)
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

        var pattern = GetRequiredString(arguments, "pattern");
        if (pattern.Length > FileToolLimits.MaxPatternLength)
        {
            throw new ArgumentException("pattern_too_long");
        }

        if (pattern.Contains('\0'))
        {
            throw new ArgumentException("invalid_pattern");
        }

        if (basePath.Length > FileToolLimits.MaxPathLength)
        {
            throw new ArgumentException("path_too_long");
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

        var baseResolved = ResolveChecked(basePath);
        var baseCanonical = baseResolved.CanonicalPath;
        if (!Directory.Exists(baseCanonical))
        {
            throw new IOException("not_a_directory");
        }

        var sw = Stopwatch.StartNew();
        var results = new List<string>();
        int skipped = 0;
        int scanned = 0;

        var stack = new Stack<string>();
        stack.Push(baseCanonical);
        var visitedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var traversalCapped = false;
        const int maxScannedEntries = FileToolLimits.MaxEntries * 16;

        // Precompile regex for pattern
        var glob = new FileGlobPattern(pattern);

        while (stack.Count > 0)
        {
            if (sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs)
            {
                break;
            }

            if (scanned >= maxScannedEntries)
            {
                traversalCapped = true;
                break;
            }

            var current = stack.Pop();
            if (!visitedDirectories.Add(current))
            {
                continue;
            }

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

                if (scanned >= maxScannedEntries)
                {
                    traversalCapped = true;
                    break;
                }

                // Count every enumerated entry, including skipped or invalid
                // links, so a hostile directory cannot evade the traversal cap.
                scanned++;

                ResolvedPath entryResolved;
                try
                {
                    entryResolved = _resolver.Resolve(entry);
                }
                catch (ArgumentException)
                {
                    skipped++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    skipped++;
                    continue;
                }

                if (entryResolved.IsProtected || entryResolved.IsOutsideWorkspace)
                {
                    skipped++;
                    continue;
                }

                var relative = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, entryResolved.CanonicalPath);
                // For glob matching, we match relative path normalized with '/' against pattern
                // If pattern does not contain '/', we also match file name only
                bool matches = false;
                try
                {
                    matches = glob.IsMatch(relative);
                }
                catch
                {
                    matches = false;
                }

                if (matches)
                {
                    results.Add(relative);
                }

                bool isDir = Directory.Exists(entryResolved.CanonicalPath);
                if (isDir)
                {
                    // Only push directories, but bounded
                    if (scanned < maxScannedEntries)
                    {
                        stack.Push(entryResolved.CanonicalPath);
                    }
                    else
                    {
                        traversalCapped = true;
                        skipped++;
                    }
                }

                if (results.Count > maxResults * 4 && scanned > maxResults * 10)
                {
                    // Early bound to avoid unbounded accumulation
                    if (results.Count >= maxResults)
                    {
                        break;
                    }
                }
            }
        }

        results.Sort(StringComparer.Ordinal);

        bool truncated = false;
        List<string> final;
        if (results.Count > maxResults)
        {
            truncated = true;
            final = results.Take(maxResults).ToList();
        }
        else
        {
            truncated = traversalCapped || sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs;
            final = results;
            if (final.Count > maxResults)
            {
                truncated = true;
                final = final.Take(maxResults).ToList();
            }
        }

        // If we hit maxResults exactly and there were more potential matches, truncated should be true
        if (!truncated && results.Count == maxResults && scanned > results.Count)
        {
            // We can't know if more matches exist without exhaustive scan, but if we stopped early due to caps, truncated
            // For deterministic, if we collected exactly maxResults and scanned limit not hit, truncated false
            // Keep as is
        }

        var arr = new JsonArray(final.Select(p => (JsonNode)JsonValue.Create(p)).ToArray());
        return new JsonObject
        {
            ["results"] = arr,
            ["count"] = final.Count,
            ["truncated"] = truncated,
            ["skipped"] = skipped,
            ["scanned"] = scanned,
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
