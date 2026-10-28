using System.Text;
using System.Text.Json.Nodes;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

public sealed partial class FileEditAdapter
{
    public const int MaximumEdits = 64;

    /// <summary>All matches refer to the original file. Commit once after validating every edit.</summary>
    public JsonObject ExecuteMany(JsonObject arguments)
    {
        var path = GetRequiredString(arguments, "path");
        var expected = GetRequiredString(arguments, "expected_sha256");
        FileHashService.ValidateExpectedSha256(expected);
        var resolved = _resolver.Resolve(path);
        if (resolved.IsProtected) throw new UnauthorizedAccessException("protected_path");
        if (resolved.IsOutsideWorkspace) throw new UnauthorizedAccessException("outside_workspace");
        if (!File.Exists(resolved.CanonicalPath)) throw new FileNotFoundException("file_not_found");
        if (!string.Equals(FileHashService.GetCurrentHash(resolved.CanonicalPath), expected, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("workspace_changed");
        var original = ReadBoundedText(resolved.CanonicalPath);
        var text = NormalizeToLf(original);
        if (arguments["edits"] is not JsonArray edits || edits.Count is < 1 or > MaximumEdits)
            throw new ArgumentException("invalid_edits");

        var replacements = new List<(int Start, int Length, string Text)>();
        var totalInputBytes = 0;
        foreach (var item in edits)
        {
            if (item is not JsonObject edit) throw new ArgumentException("invalid_edits");
            var oldText = NormalizeToLf(GetRequiredString(edit, "old_string"));
            var newText = NormalizeToLf(GetRequiredString(edit, "new_string"));
            if (oldText.Length == 0) throw new ArgumentException("old_string_empty");
            ValidateUtf8(oldText);
            ValidateUtf8(newText);
            totalInputBytes = checked(totalInputBytes + Encoding.UTF8.GetByteCount(oldText) + Encoding.UTF8.GetByteCount(newText));
            if (totalInputBytes > MaxInputBytes) throw new ArgumentException("content_too_large");
            var expectedCount = 1;
            if (edit.ContainsKey("expected_occurrences")
                && (edit["expected_occurrences"] is not JsonValue value || !value.TryGetValue<int>(out expectedCount)
                    || expectedCount is < 1 or > 1024))
                throw new ArgumentException("invalid_expected_occurrences");
            var count = CountOccurrences(text, oldText);
            if (count == 0) throw new EditMatchException("edit_no_match", 0);
            if (count != expectedCount) throw new EditMatchException("edit_not_unique", count);
            for (var offset = 0; offset <= text.Length - oldText.Length;)
            {
                var start = text.IndexOf(oldText, offset, StringComparison.Ordinal);
                if (start < 0) break;
                replacements.Add((start, oldText.Length, newText));
                offset = start + oldText.Length;
            }
        }

        replacements.Sort((left, right) => left.Start.CompareTo(right.Start));
        var end = 0;
        long finalLength = text.Length;
        foreach (var replacement in replacements)
        {
            if (replacement.Start < end) throw new ArgumentException("edit_overlap");
            end = replacement.Start + replacement.Length;
            finalLength += replacement.Text.Length - replacement.Length;
        }
        if (finalLength > MaxInputBytes) throw new ArgumentException("content_too_large");
        var updated = new StringBuilder((int)finalLength);
        end = 0;
        foreach (var replacement in replacements)
        {
            updated.Append(text.AsSpan(end, replacement.Start - end));
            updated.Append(replacement.Text);
            end = replacement.Start + replacement.Length;
        }
        updated.Append(text.AsSpan(end));
        // Reuse the guarded atomic replacement, including re-resolution and hash checking.
        var result = Execute(new JsonObject
        {
            ["path"] = path, ["expected_sha256"] = expected,
            ["old_string"] = original,
            ["new_string"] = DenormalizeFromLf(updated.ToString(), ShouldUseCrLf(original)),
        });
        result["replacements"] = replacements.Count;
        result["edit_count"] = edits.Count;
        return result;
    }
}
