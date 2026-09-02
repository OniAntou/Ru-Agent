namespace RuAgent.Harness.Persistence;

using System.Text;

public enum WorkspaceMergeOutcome
{
    Unchanged,
    Merged,
    LastWriterWins,
    BinaryConflict,
}

public sealed record WorkspaceMergeResult(
    WorkspaceMergeOutcome Outcome,
    string? Content,
    string? ErrorCode = null);

public static class WorkspaceMergeEngine
{
    private const int MaxTextCharacters = 16 * 1024 * 1024;

    public static WorkspaceMergeResult MergeBytes(
        ReadOnlySpan<byte> baseBytes,
        ReadOnlySpan<byte> localBytes,
        ReadOnlySpan<byte> externalBytes,
        long localSequence,
        long externalSequence,
        string? extension = null)
    {
        if (baseBytes.SequenceEqual(localBytes) && baseBytes.SequenceEqual(externalBytes))
        {
            return new WorkspaceMergeResult(WorkspaceMergeOutcome.Unchanged, DecodeText(baseBytes));
        }

        if (!LooksText(baseBytes, extension)
            || !LooksText(localBytes, extension)
            || !LooksText(externalBytes, extension))
        {
            return RejectBinaryConflict();
        }

        return MergeText(
            DecodeText(baseBytes),
            DecodeText(localBytes),
            DecodeText(externalBytes),
            localSequence,
            externalSequence);
    }

    public static WorkspaceMergeResult MergeText(
        string baseContent,
        string localContent,
        string externalContent,
        long localSequence,
        long externalSequence)
    {
        ArgumentNullException.ThrowIfNull(baseContent);
        ArgumentNullException.ThrowIfNull(localContent);
        ArgumentNullException.ThrowIfNull(externalContent);
        if (baseContent.Length > MaxTextCharacters
            || localContent.Length > MaxTextCharacters
            || externalContent.Length > MaxTextCharacters)
        {
            throw new ArgumentOutOfRangeException(nameof(baseContent), "Text merge input exceeds the configured bound.");
        }

        var newline = DetectNewline(baseContent);
        var baseLines = SplitLines(baseContent);
        var localLines = SplitLines(localContent);
        var externalLines = SplitLines(externalContent);
        if (localContent == externalContent)
        {
            return new WorkspaceMergeResult(WorkspaceMergeOutcome.Unchanged, localContent);
        }

        if (localContent == baseContent)
        {
            return new WorkspaceMergeResult(WorkspaceMergeOutcome.Merged, externalContent);
        }

        if (externalContent == baseContent)
        {
            return new WorkspaceMergeResult(WorkspaceMergeOutcome.Merged, localContent);
        }

        if (baseLines.Count == localLines.Count && baseLines.Count == externalLines.Count)
        {
            var merged = new string[baseLines.Count];
            var overlapping = false;
            for (var index = 0; index < merged.Length; index++)
            {
                var localChanged = !string.Equals(localLines[index], baseLines[index], StringComparison.Ordinal);
                var externalChanged = !string.Equals(externalLines[index], baseLines[index], StringComparison.Ordinal);
                if (localChanged && externalChanged)
                {
                    overlapping = true;
                    merged[index] = localLines[index] == externalLines[index]
                        ? localLines[index]
                        : localSequence >= externalSequence ? localLines[index] : externalLines[index];
                }
                else
                {
                    merged[index] = localChanged ? localLines[index] : externalLines[index];
                }
            }

            return new WorkspaceMergeResult(
                overlapping ? WorkspaceMergeOutcome.LastWriterWins : WorkspaceMergeOutcome.Merged,
                JoinLines(merged, newline, HasTrailingNewline(baseContent)));
        }

        return localSequence >= externalSequence
            ? new WorkspaceMergeResult(WorkspaceMergeOutcome.LastWriterWins, localContent)
            : new WorkspaceMergeResult(WorkspaceMergeOutcome.LastWriterWins, externalContent);
    }

    public static WorkspaceMergeResult RejectBinaryConflict() =>
        new(WorkspaceMergeOutcome.BinaryConflict, null, "concurrent_binary_conflict");

    private static bool LooksText(ReadOnlySpan<byte> bytes, string? extension)
    {
        if (bytes.Length > MaxTextCharacters * 4)
        {
            return false;
        }

        if (extension is not null && new[] { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".ico", ".pdf", ".zip", ".7z", ".exe", ".dll" }
            .Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var sampleLength = Math.Min(bytes.Length, 4096);
        var utf16Bom = HasPrefix(bytes, 0xFF, 0xFE) || HasPrefix(bytes, 0xFE, 0xFF);
        if (!utf16Bom && bytes[..sampleLength].IndexOf((byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = utf16Bom
                ? (bytes[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(bytes[2..])
                : StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (HasPrefix(bytes, 0xFF, 0xFE))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (HasPrefix(bytes, 0xFE, 0xFF))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        return StrictUtf8.GetString(bytes);
    }

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static bool HasPrefix(ReadOnlySpan<byte> value, byte first, byte second) =>
        value.Length >= 2 && value[0] == first && value[1] == second;

    private static List<string> SplitLines(string content) =>
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();

    private static string DetectNewline(string content) =>
        content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static bool HasTrailingNewline(string content) =>
        content.EndsWith("\n", StringComparison.Ordinal) || content.EndsWith("\r", StringComparison.Ordinal);

    private static string JoinLines(IReadOnlyList<string> lines, string newline, bool trailingNewline)
    {
        var result = string.Join(newline, lines);
        if (trailingNewline && !result.EndsWith(newline, StringComparison.Ordinal))
        {
            result += newline;
        }

        return result;
    }
}
