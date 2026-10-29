using System.Text;

namespace RuAgent.Harness.Core.Context;

/// <summary>Heuristic samples supplement head/tail excerpts, never replace original evidence.</summary>
internal static class ToolDiagnosticExcerpt
{
    private const int MaximumScanChars = 1024 * 1024;
    private const int MaximumLines = 6;
    private const int MaximumLineChars = 300;
    private static readonly string[] Markers = ["error ", "error:", "failed", "exception", "traceback", "fatal ", "not ok", "assertionerror"];

    public static string Select(string content, int start, int end)
    {
        var scanEnd = Math.Min(end, start + MaximumScanChars);
        var builder = new StringBuilder();
        var position = start;
        // The head may end partway through a line; don't present its suffix as a new diagnostic.
        if (position > 0 && content[position - 1] != '\n')
        {
            var next = content.AsSpan(position, scanEnd - position).IndexOf('\n');
            if (next < 0) return "";
            position += next + 1;
        }
        var count = 0;
        while (position < scanEnd && count < MaximumLines)
        {
            var remaining = content.AsSpan(position, scanEnd - position);
            var newline = remaining.IndexOf('\n');
            var length = newline < 0 ? remaining.Length : newline;
            var line = remaining[..length];
            var markerIndex = -1;
            foreach (var marker in Markers)
            {
                var found = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (found >= 0 && (markerIndex < 0 || found < markerIndex)) markerIndex = found;
            }
            if (markerIndex >= 0)
            {
                var from = Math.Max(0, markerIndex - 80);
                if (from > 0 && char.IsLowSurrogate(line[from])) from++;
                var take = Math.Min(length - from, MaximumLineChars);
                if (take > 0 && char.IsHighSurrogate(line[from + take - 1])) take--;
                builder.Append("\n- ");
                if (from > 0) builder.Append('…');
                builder.Append(line.Slice(from, take));
                if (from + take < length) builder.Append('…');
                count++;
            }
            position += length + (newline < 0 ? 0 : 1);
        }
        return count == 0 ? "" : "\n[selected diagnostic lines from omitted middle; heuristic, incomplete]"
            + builder + "\n[end selected diagnostics]\n";
    }
}
