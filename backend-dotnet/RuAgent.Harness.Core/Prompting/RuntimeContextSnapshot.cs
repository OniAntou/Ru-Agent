using System.Text;
using RuAgent.Harness.Core.Context;

namespace RuAgent.Harness.Core.Prompting;

/// <summary>Run-local, untrusted context admitted before dispatch and replayed at its original position.</summary>
public static class RuntimeContextSnapshot
{
    public const string SourcePrefix = "runtime-snapshot:";
    public const int MaximumBytes = 128 * 1024;
    public const string Cleared = "Current runtime/workflow context is empty. Earlier runtime-context snapshots no longer apply.";

    public static bool IsSnapshot(ModelContextEntry entry) =>
        entry.Kind == ModelContextEntryKind.Text && entry.Role == ModelContextRole.User
        && entry.SourceId.StartsWith(SourcePrefix, StringComparison.Ordinal);

    public static string? Render(string? runtime, string? workflow)
    {
        var sections = new[] { workflow, runtime }.Where(text => !string.IsNullOrWhiteSpace(text));
        var body = string.Join("\n\n", sections);
        if (body.Length == 0) return null;
        return "RUNTIME CONTEXT SNAPSHOT — supersedes earlier runtime-context snapshots; historical observations are not current authority.\n"
            + SystemPromptComposer.UntrustedDataWarning + "\n" + body;
    }

    public static bool Fits(string? text) => text is null || Encoding.UTF8.GetByteCount(text) <= MaximumBytes;

    public static string? Pending(ModelContextSnapshot context, string? current, IReadOnlySet<string>? visibleSnapshotTexts = null)
    {
        var retained = context.Entries.LastOrDefault(entry => IsSnapshot(entry)
            && (visibleSnapshotTexts is null || visibleSnapshotTexts.Contains(entry.Content!)))?.Content;
        if (retained is null && current is null) return null;
        var text = current ?? Cleared;
        return string.Equals(retained, text, StringComparison.Ordinal) ? null : text;
    }
}
