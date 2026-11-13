using System.Text;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Formats a read-only review of one subagent change as a unified patch a user can
/// save or feed to <c>git apply</c>. It never applies the patch and never fetches
/// the base file; both sides must already be part of the bounded review response.
/// </summary>
internal static class SubagentPatchFormatter
{
    internal const int MaxTextChars = 20_000;
    internal const int MaxLines = 800;

    internal static string? Format(string path, string? before, string? after)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')
            || path.Contains('\r') || path.Contains('\n')
            || before is null && after is null)
        {
            return null;
        }

        if (before is not null && (before.Length > MaxTextChars || CountLines(before) > MaxLines))
        {
            return null;
        }

        if (after is not null && (after.Length > MaxTextChars || CountLines(after) > MaxLines))
        {
            return null;
        }

        var normalizedBefore = Normalize(before);
        var normalizedAfter = Normalize(after);
        var lines = DiffEngine.Compute(normalizedBefore, normalizedAfter);
        if (lines.Count == 0 || lines.All(line => line.Kind == DiffKind.Unchanged)) return null;

        int oldCount = CountLines(normalizedBefore);
        int newCount = CountLines(normalizedAfter);
        var builder = new StringBuilder();
        builder.Append(before is null ? "--- /dev/null\n" : $"--- a/{path}\n");
        builder.Append(after is null ? "+++ /dev/null\n" : $"+++ b/{path}\n");
        builder.Append($"@@ -{(oldCount == 0 ? 0 : 1)},{oldCount} +{(newCount == 0 ? 0 : 1)},{newCount} @@\n");
        foreach (var line in lines)
        {
            builder.Append(line.Kind switch
            {
                DiffKind.Added => '+',
                DiffKind.Removed => '-',
                _ => ' ',
            });
            builder.Append(line.Text).Append('\n');
        }

        return builder.ToString();
    }

    private static int CountLines(string? text) => string.IsNullOrEmpty(text)
        ? 0
        : text.Split('\n').Length;

    /// <summary>
    /// A patch hunk counts file lines, not the empty segment after a final newline.
    /// </summary>
    private static string? Normalize(string? text) => string.IsNullOrEmpty(text)
        ? null
        : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').TrimEnd('\n');
}
