using System.Collections.Concurrent;

namespace RuAgent.Broker.Skills;

public sealed partial class SkillCatalog
{
    private static readonly ConcurrentDictionary<string, CacheEntry> SharedCache = new(StringComparer.OrdinalIgnoreCase);
    private const long MaximumCacheBytes = 32 * 1024 * 1024;

    private bool IsFresh(CacheEntry cache, IReadOnlyList<string> roots)
    {
        if (DateTimeOffset.UtcNow - cache.CreatedAt > TimeSpan.FromSeconds(30)
            || cache.RootStamps.Count != roots.Count) return false;
        for (var index = 0; index < roots.Count; index++)
            if (Stamp(roots[index], directory: true) != cache.RootStamps[index]) return false;
        if (!DirectoryStamps(roots).OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(cache.DirectoryStamps.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))) return false;
        foreach (var stamp in cache.FileStamps)
            if (Stamp(stamp.Key, directory: false) != stamp.Value) return false;
        return Stamp(_preferencesPath, directory: false) == cache.PreferencesStamp;
    }

    private CacheEntry CreateCacheEntry(SkillCatalogResult result, IReadOnlyList<string> roots)
    {
        var directories = DirectoryStamps(roots);
        // Invalid and omitted definitions also invalidate discovery when repaired.
        var files = directories.Keys.Where(path => !HasReparseAncestor(path))
            .Select(path => Path.Combine(path, "SKILL.md"))
            .ToDictionary(path => path, path => Stamp(path, directory: false), StringComparer.OrdinalIgnoreCase);
        var weight = 1024L + directories.Keys.Concat(files.Keys).Sum(path => 128L + path.Length * 2L)
            + result.Items.Sum(item => 512L + 2L * (item.Id.Length + item.Name.Length + item.Description.Length
                + item.SourcePath.Length + item.ResourceBase.Length + item.Revision.Length
                + (item.Compatibility?.Length ?? 0) + item.Requires.Sum(value => value.Length)))
            + result.Diagnostics.Sum(item => 128L + 2L * (item.SourcePath.Length + item.Code.Length));
        return new(result, roots.Select(root => Stamp(root, directory: true)).ToArray(),
            directories, files, Stamp(_preferencesPath, directory: false), weight, DateTimeOffset.UtcNow);
    }

    private static void StoreCache(string key, CacheEntry entry)
    {
        if (entry.Weight > MaximumCacheBytes) return;
        SharedCache[key] = entry;
        while (SharedCache.Count > 64 || SharedCache.Values.Sum(item => item.Weight) > MaximumCacheBytes)
        {
            var oldest = SharedCache.OrderBy(pair => pair.Value.CreatedAt).FirstOrDefault();
            if (oldest.Key is null) break;
            SharedCache.TryRemove(oldest.Key, out _);
        }
    }

    private static IReadOnlyDictionary<string, FileStamp> DirectoryStamps(IReadOnlyList<string> roots)
    {
        var values = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (HasReparseAncestor(root)) continue;
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).Take(MaxDirectoriesPerRoot + 1))
                    values[directory] = Stamp(directory, directory: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        return values;
    }

    private static FileStamp Stamp(string path, bool directory)
    {
        try
        {
            if (directory)
            {
                var info = new DirectoryInfo(path);
                return info.Exists ? new(info.LastWriteTimeUtc.Ticks, 1) : new(0, 0);
            }
            var file = new FileInfo(path);
            return file.Exists ? new(file.LastWriteTimeUtc.Ticks, file.Length) : new(0, 0);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return new(-1, -1); }
    }

    private sealed record FileStamp(long LastWriteTicks, long Length);
    private sealed record CacheEntry(
        SkillCatalogResult Result,
        IReadOnlyList<FileStamp> RootStamps,
        IReadOnlyDictionary<string, FileStamp> DirectoryStamps,
        IReadOnlyDictionary<string, FileStamp> FileStamps,
        FileStamp PreferencesStamp, long Weight, DateTimeOffset CreatedAt);
}
