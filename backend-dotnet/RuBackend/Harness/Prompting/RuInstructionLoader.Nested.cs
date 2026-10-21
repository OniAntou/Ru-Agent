using System.Security.Cryptography;
using System.Text;

namespace RuBackend.Harness.Prompting;

public sealed partial class RuInstructionLoader
{
    private const int MaximumNestedDirectories = 8;
    private const int MaximumNestedDepth = 16;

    private static IEnumerable<(string SourceId, string RelativePath)> NestedCandidates(
        string root, IReadOnlyList<string>? observedPaths, List<RuInstructionSkip> skips)
    {
        if (observedPaths is null) yield break;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in observedPaths.TakeLast(128))
        {
            string candidate;
            try { candidate = Path.GetFullPath(path, root); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (!IsContained(root, candidate) || !TryResolveFinalPath(candidate, out var final) || !IsContained(root, final)) continue;
            var relative = Path.GetRelativePath(root, Path.GetDirectoryName(candidate)!);
            var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length > MaximumNestedDepth) { skips.Add(new RuInstructionSkip("nested:depth", "depth_limit")); continue; }
            for (var count = 1; count <= segments.Length; count++)
            {
                var directory = Path.Combine(segments.Take(count).ToArray());
                if (directory != "." && directory != ".ru") directories.Add(directory);
            }
        }
        // Empty ancestors must not consume instruction slots. Select the most specific existing
        // files first, then render broad-to-specific so the nearest selected guidance wins.
        var available = directories.Where(directory => File.Exists(Path.Combine(root, directory, "AGENTS.md")))
            .OrderByDescending(path => path.Count(character => character is '\\' or '/'))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (available.Length > MaximumNestedDirectories) skips.Add(new RuInstructionSkip("nested:count", "directory_limit"));
        foreach (var directory in available.Take(MaximumNestedDirectories)
                     .OrderBy(path => path.Count(character => character is '\\' or '/'))
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.Combine(directory, "AGENTS.md");
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(relative.ToUpperInvariant()))).ToLowerInvariant();
            yield return ("nested:" + digest[..24], relative);
        }
    }
}
