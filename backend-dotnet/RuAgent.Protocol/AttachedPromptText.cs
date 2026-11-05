using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RuAgent.Protocol;

/// <summary>One text document that a composer inlined ahead of the typed prompt.</summary>
public sealed record AttachedPromptDocument(string FileName, string SizeLabel, long? ApproximateBytes, string Language, string Content);

/// <summary>A prompt split back into its inlined documents and the text the user typed.</summary>
public sealed record AttachedPromptParts(IReadOnlyList<AttachedPromptDocument> Documents, string Text);

/// <summary>
/// Reads the attachment envelope the GUI and CLI composers write in front of a prompt:
/// for each document a <c>[Attached file: name (size)]</c> line, a fenced block with the
/// file text, and a blank line; then the typed text. The run wire has no attachment
/// field, so stored history only has this prompt. Display surfaces split it back so a
/// reopened session shows the typed text with file chips instead of the raw envelope.
/// </summary>
/// <remarks>
/// Only a prompt that starts with the envelope is split, and only when every block is
/// well formed; anything else is returned as plain text. A document may itself contain
/// a line that is just a fence, so each block's closing fence is chosen among the
/// candidates by the recorded size, which both composers write from the source bytes.
/// </remarks>
public static class AttachedPromptText
{
    private const string Fence = "```";

    private static readonly Regex Header = new(
        @"^\[Attached file: (?<name>.+) \((?<size>[^()]*)\)\]$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(250));

    private static readonly Regex Size = new(
        @"^(?<value>\d+(?:\.\d+)?) ?(?<unit>B|KB|MB)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(250));

    /// <summary>True when <paramref name="prompt"/> starts with an attachment envelope.</summary>
    public static bool StartsWithAttachment(string? prompt) =>
        prompt is not null && prompt.StartsWith("[Attached file: ", StringComparison.Ordinal);

    /// <summary>
    /// Splits <paramref name="prompt"/> into documents and typed text, or returns null when it
    /// does not start with a well-formed envelope.
    /// </summary>
    public static AttachedPromptParts? TrySplit(string? prompt)
    {
        if (!StartsWithAttachment(prompt)) return null;
        var lines = prompt!.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var documents = new List<AttachedPromptDocument>();
        var index = 0;
        while (index < lines.Length && Header.Match(lines[index]) is { Success: true } header)
        {
            if (index + 1 >= lines.Length || !lines[index + 1].StartsWith(Fence, StringComparison.Ordinal)) return null;
            var language = lines[index + 1][Fence.Length..];
            if (language.Contains('`', StringComparison.Ordinal)) return null;
            var sizeLabel = header.Groups["size"].Value;
            var approximateBytes = ParseSize(sizeLabel);
            var contentStart = index + 2;
            var close = ChooseClosingFence(lines, contentStart, approximateBytes);
            if (close < 0) return null;
            documents.Add(new AttachedPromptDocument(
                header.Groups["name"].Value, sizeLabel, approximateBytes, language,
                string.Join("\n", lines[contentStart..close])));
            // The composer writes one blank line after each block; the prompt is trimmed
            // at the end, so the last block may end the text.
            index = close + 1;
            if (index < lines.Length)
            {
                if (lines[index].Length != 0) return null;
                index++;
            }
        }

        var text = index < lines.Length ? string.Join("\n", lines[index..]).Trim() : string.Empty;
        return documents.Count == 0 ? null : new AttachedPromptParts(documents, text);
    }

    /// <summary>
    /// The typed text of an attachment prompt, or the first file name when nothing was typed;
    /// any other prompt is returned unchanged. Used for titles and one-line summaries.
    /// </summary>
    public static string SummaryText(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (TrySplit(prompt) is not { } parts) return prompt;
        return parts.Text.Length > 0 ? parts.Text : parts.Documents[0].FileName;
    }

    // A closing fence is a line that is exactly the fence, followed by the end of the
    // prompt or a blank line. Several candidates are possible when the file itself holds
    // such a line; the recorded size picks the one whose content length fits best.
    private static int ChooseClosingFence(string[] lines, int contentStart, long? approximateBytes)
    {
        var best = -1;
        var bestDistance = long.MaxValue;
        long bytes = 0;
        for (var line = contentStart; line < lines.Length; line++)
        {
            if (line > contentStart) bytes += 1; // the newline before this line
            if (lines[line] == Fence && (line + 1 == lines.Length || lines[line + 1].Length == 0))
            {
                if (approximateBytes is null) return line;
                // The newline in front of the fence ends the block; it is not file content.
                var contentBytes = line > contentStart ? bytes - 1 : 0;
                var distance = Math.Abs(contentBytes - approximateBytes.Value);
                if (distance < bestDistance)
                {
                    best = line;
                    bestDistance = distance;
                }
            }

            bytes += Encoding.UTF8.GetByteCount(lines[line]);
        }

        return best;
    }

    private static long? ParseSize(string label)
    {
        if (Size.Match(label) is not { Success: true } match
            || !double.TryParse(match.Groups["value"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            return null;
        var scale = match.Groups["unit"].Value switch { "KB" => 1024d, "MB" => 1024d * 1024d, _ => 1d };
        return (long)Math.Round(value * scale);
    }
}
