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
}
