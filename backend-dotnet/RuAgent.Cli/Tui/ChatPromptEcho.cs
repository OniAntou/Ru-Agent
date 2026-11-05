namespace RuAgent.Cli.Tui;

/// <summary>Append-only prompt echo; no frame, cursor movement or resize state.</summary>
public static class ChatPromptEcho
{
    public static string Format(string text, IReadOnlyList<string>? attachmentLabels = null)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var result = string.Join(Environment.NewLine, lines.Select(line => "> " + ChatDisplayText.Sanitize(line, 16_000)));
        if (attachmentLabels is { Count: > 0 })
            result += Environment.NewLine + string.Join(Environment.NewLine,
                attachmentLabels.Select(label => "  " + ChatDisplayText.Sanitize(label, 512)));
        return result;
    }

    /// <summary>
    /// Echoes a prompt read back from history. A prompt whose text attachments were
    /// inlined by the composer is shown as it was sent: the typed text and one
    /// <c>[file n: name]</c> label per document, not the inlined envelope and file text.
    /// </summary>
    public static string FormatStored(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (RuAgent.Protocol.AttachedPromptText.TrySplit(prompt) is not { } parts) return Format(prompt);
        var labels = parts.Documents.Select((document, index) => $"[file {index + 1}: {document.FileName}]").ToList();
        return Format(parts.Text, labels);
    }
}
