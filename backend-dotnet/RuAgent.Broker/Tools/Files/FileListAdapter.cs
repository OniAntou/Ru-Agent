using System.Diagnostics;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed class FileListAdapter
{
    private readonly string _workspaceRoot;
    private readonly CanonicalPathResolver _resolver;

    public FileListAdapter(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _resolver = new CanonicalPathResolver(_workspaceRoot);
    }

    public JsonObject Execute(JsonObject arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var path = GetOptionalString(arguments, "path") ?? ".";
        if (string.IsNullOrWhiteSpace(path))
        {
            path = ".";
        }

        if (path.Length > FileToolLimits.MaxPathLength)
        {
            throw new ArgumentException("path_too_long");
        }

        var maxEntries = FileToolLimits.MaxEntries;
        if (arguments.TryGetPropertyValue("max_entries", out var maxNode) && maxNode is not null)
        {
            if (maxNode is JsonValue mv && mv.TryGetValue<int>(out var v))
            {
                if (v < 1 || v > FileToolLimits.MaxEntries)
                {
                    throw new ArgumentException("invalid_max_entries");
                }

                maxEntries = v;
            }
            else
            {
                throw new ArgumentException("invalid_max_entries");
            }
        }

        var resolved = ResolveChecked(path);
        var canonical = resolved.CanonicalPath;
        if (!Directory.Exists(canonical))
        {
            throw new IOException("not_a_directory");
        }

        var sw = Stopwatch.StartNew();
        var collected = new List<(string Relative, bool IsDir, long Size)>();
        int skipped = 0;
        int scannedEntries = 0;
        var scanLimitHit = false;
        const int maxScannedEntries = FileToolLimits.MaxEntries * 16;

        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(canonical);
        }
        catch (UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException("access_denied");
        }
        catch (DirectoryNotFoundException)
        {
            throw new IOException("not_a_directory");
        }

        foreach (var entry in entries)
        {
            if (sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs)
            {
                break;
            }

            if (scannedEntries++ >= maxScannedEntries)
            {
                scanLimitHit = true;
                break;
            }

            if (collected.Count >= FileToolLimits.MaxEntries * 2)
            {
                // Bounded enumeration: avoid collecting unbounded for huge directories
                scanLimitHit = true;
                break;
            }

            try
            {
                var entryResolved = _resolver.Resolve(entry);
                if (entryResolved.IsProtected || entryResolved.IsOutsideWorkspace)
                {
                    skipped++;
                    continue;
                }

                var relative = WorkspacePathGuard.ToWorkspaceRelative(_resolver.CanonicalRoot, entryResolved.CanonicalPath);
                bool isDir = Directory.Exists(entryResolved.CanonicalPath);
                long size = 0;
                if (!isDir)
                {
                    try
                    {
                        var info = new FileInfo(entryResolved.CanonicalPath);
                        if (info.Exists)
                        {
                            size = info.Length;
                        }
                    }
                    catch
                    {
                        size = 0;
                    }
                }

                collected.Add((relative, isDir, size));
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
            catch (ArgumentException)
            {
                skipped++;
                continue;
            }
        }

        collected.Sort((a, b) => string.Compare(a.Relative, b.Relative, StringComparison.Ordinal));

        bool truncated = false;
        List<(string Relative, bool IsDir, long Size)> finalList;
        if (collected.Count > maxEntries)
        {
            truncated = true;
            finalList = collected.Take(maxEntries).ToList();
        }
        else
        {
            truncated = scanLimitHit || sw.ElapsedMilliseconds > FileToolLimits.MaxTimeMs;
            // Also check if directory had more entries than we collected due to bounded enumeration cap
            // We already limited collected to MaxEntries*2, so if we hit that cap, truncated
            if (collected.Count >= FileToolLimits.MaxEntries * 2)
            {
                truncated = true;
                finalList = collected.Take(maxEntries).ToList();
            }
            else
            {
                finalList = collected;
            }
        }

        var jsonEntries = new JsonArray();
        foreach (var (rel, isDir, size) in finalList)
        {
            jsonEntries.Add(new JsonObject
            {
                ["path"] = rel,
                ["kind"] = isDir ? "directory" : "file",
                ["size"] = size,
            });
        }

        return new JsonObject
        {
            ["entries"] = jsonEntries,
            ["count"] = finalList.Count,
            ["truncated"] = truncated,
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

    private static string? GetOptionalString(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;
}
