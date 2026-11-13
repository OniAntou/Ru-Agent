using System.Text;

namespace RuApp.Services;

/// <summary>
/// Formats a read-only review of one subagent change as a unified patch a user can
/// save or feed to <c>git apply</c>. It never applies the patch and never fetches
/// the base file; both sides must already be part of the bounded review response.
/// </summary>
/// <remarks>
/// A null side means the file does not exist on that side (<c>/dev/null</c>); an
/// empty string is an existing empty file. Line endings and a missing final newline
/// are preserved, because a patch that normalizes them does not apply.
/// </remarks>
internal static class SubagentPatchFormatter
{
    // The Host bounds each side of a review at 64 KiB chars; anything larger was truncated.
    internal const int MaxTextChars = 64 * 1024;
    private const string NoNewlineMarker = "\\ No newline at end of file\n";

    /// <param name="computed">The diff of the same two sides, when the caller already has it.</param>
    internal static string? Format(string path, string? before, string? after, SubagentFileDiff? computed = null)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0')
            || path.Contains('\r') || path.Contains('\n')
            || before is null && after is null
            || before?.Length > MaxTextChars || after?.Length > MaxTextChars)
        {
            return null;
        }

        var diff = computed ?? SubagentDiff.Compute(before, after);
        // No hunk also covers creating or deleting an empty file: this header form
        // cannot express that, so offer no patch rather than a no-op.
        if (diff is null || diff.Hunks.Count == 0) return null;

        var builder = new StringBuilder();
        builder.Append(before is null ? "--- /dev/null\n" : $"--- a/{path}\n");
        builder.Append(after is null ? "+++ /dev/null\n" : $"+++ b/{path}\n");
        foreach (var hunk in diff.Hunks)
        {
            builder.Append($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@\n");
            foreach (var line in hunk.Lines)
            {
                builder.Append(line.Op switch
                {
                    SubagentDiffOp.Added => '+',
                    SubagentDiffOp.Removed => '-',
                    _ => ' ',
                });
                builder.Append(line.Text).Append('\n');
                if (line.NoNewlineAtEnd) builder.Append(NoNewlineMarker);
            }
        }

        return builder.ToString();
    }
}
