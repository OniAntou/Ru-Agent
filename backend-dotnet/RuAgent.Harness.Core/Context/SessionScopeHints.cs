using System.Collections.ObjectModel;
using System.Text;

namespace RuAgent.Harness.Core.Context;

/// <summary>Host-authored, bounded directory hints carried between visible root runs.</summary>
public static class SessionScopeHints
{
    public const string SourceId = "base:session-scope-hints";
    public const int MaximumPaths = 16;
    public const int MaximumPathUtf8Bytes = 4096;
    public const int MaximumUtf8Bytes = 16 * 1024;

    private const string PathPrefix = "observed_path_b64: ";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static string Encode(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var lines = new List<string>(Math.Min(paths.Count, MaximumPaths));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedBytes = 0;
        for (var index = paths.Count - 1; index >= 0; index--)
        {
            var path = paths[index];
            if (path is not { Length: > 0 and <= MaximumPathUtf8Bytes }) continue;
            byte[] bytes;
            try { bytes = StrictUtf8.GetBytes(path); }
            catch (EncoderFallbackException) { continue; }
            if (bytes.Length > MaximumPathUtf8Bytes || !known.Add(path)) continue;
            var line = PathPrefix + Convert.ToBase64String(bytes) + "\n";
            var lineBytes = Encoding.UTF8.GetByteCount(line);
            if (lines.Count == MaximumPaths) break;
            if (usedBytes + lineBytes > MaximumUtf8Bytes) continue;
            lines.Add(line);
            usedBytes += lineBytes;
        }
        lines.Reverse();
        return string.Concat(lines);
    }

    public static IReadOnlyList<string> Decode(ModelContextEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.SourceId != SourceId || entry.Role != ModelContextRole.User
            || entry.Kind != ModelContextEntryKind.Text || entry.Content is not { } content
            || Encoding.UTF8.GetByteCount(content) > MaximumUtf8Bytes)
            return Array.Empty<string>();

        var paths = new List<string>(MaximumPaths);
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in content.Split('\n'))
        {
            if (paths.Count == MaximumPaths) break;
            if (!line.StartsWith(PathPrefix, StringComparison.Ordinal)) continue;
            try
            {
                var bytes = Convert.FromBase64String(line[PathPrefix.Length..]);
                if (bytes.Length is 0 or > MaximumPathUtf8Bytes) continue;
                var path = StrictUtf8.GetString(bytes);
                if (path.Length > 0 && known.Add(path)) paths.Add(path);
            }
            catch (Exception error) when (error is FormatException or DecoderFallbackException) { }
        }
        return new ReadOnlyCollection<string>(paths);
    }
}
