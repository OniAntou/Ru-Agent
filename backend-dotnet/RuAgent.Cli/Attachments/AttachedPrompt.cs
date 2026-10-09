using System.Text;
using RuAgent.Cli.Images;

namespace RuAgent.Cli.Attachments;

/// <summary>
/// Builds the prompt a turn actually sends: pending text/code attachments are inlined
/// as fenced blocks ahead of the typed text, exactly like the WinUI surface composes
/// them. The run wire has no text-attachment field, so the prompt is the delivery.
/// </summary>
public static class AttachedPrompt
{
    public static string Compose(string text, IReadOnlyList<CliTextAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return text;
        }

        var builder = new StringBuilder();
        foreach (var document in attachments)
        {
            builder.AppendLine($"[Attached file: {document.FileName} ({CliImageAttachment.FormatBytes(document.ByteSize)})]");
            builder.AppendLine($"```{document.Extension}");
            builder.AppendLine(document.TextContent);
            builder.AppendLine("```");
            builder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            builder.AppendLine(text);
        }

        return builder.ToString().TrimEnd();
    }
}
