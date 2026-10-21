using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Context;
using RuAgent.Harness.Core.Prompting;

namespace RuBackend.Harness.Prompting;

public sealed partial class RuPromptContextProvider
{
    private IReadOnlyList<string> ObservedFilePaths(ModelContextSnapshot context)
    {
        var paths = new List<string>();
        // Durable receipt-derived metadata works in every request mode. Older/native snapshots
        // remain a fallback for context sources that do not supply that metadata.
        var previous = context.ObservedFilePaths.Count == 0
            ? context.Entries.LastOrDefault(RuntimeContextSnapshot.IsSnapshot)?.Content : null;
        if (previous is not null)
        {
            foreach (var line in previous.Split('\n').Where(line => line.StartsWith("observed_path_b64: ", StringComparison.Ordinal)).Take(128))
            {
                try
                {
                    var bytes = Convert.FromBase64String(line[19..]);
                    if (bytes.Length <= 4096) paths.Add(new System.Text.UTF8Encoding(false, true).GetString(bytes));
                }
                catch (Exception error) when (error is FormatException or System.Text.DecoderFallbackException) { }
            }
        }
        paths.AddRange(context.ObservedFilePaths.Count > 0
            ? context.ObservedFilePaths : FileObservationPaths.Read(context.Entries));
        // Remember directories, not every file: reading another sibling must not append a redundant baseline.
        return paths.Select(NormalizeObservedScope).Where(path => path is not null).Select(path => path!)
            .Reverse().Distinct(StringComparer.OrdinalIgnoreCase).Take(16).Reverse().ToArray();
    }

    private string? NormalizeObservedScope(string path)
    {
        try
        {
            var full = Path.GetFullPath(path, _canonicalRoot);
            var relative = Path.GetRelativePath(_canonicalRoot, full);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return null;
            var directory = Path.GetDirectoryName(full);
            if (directory is null || string.Equals(Path.TrimEndingDirectorySeparator(directory),
                    Path.TrimEndingDirectorySeparator(_canonicalRoot), StringComparison.OrdinalIgnoreCase)) return null;
            return Path.Combine(directory, ".ru-instruction-scope");
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
