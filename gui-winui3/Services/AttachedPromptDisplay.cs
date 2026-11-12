using System.Collections.ObjectModel;
using RuAgent.Protocol;
using RuApp.Models;

namespace RuApp.Services;

/// <summary>
/// Restores a reopened user turn to how it looked when sent. The composer inlines text
/// attachments into the prompt (the run wire has no attachment field), so history only
/// holds that prompt; without this, a reopened session showed the raw
/// <c>[Attached file: …]</c> envelope and fenced file text instead of the typed message
/// and its file chip.
/// </summary>
internal static class AttachedPromptDisplay
{
    public static void Apply(ChatMessageItem message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (AttachedPromptText.TrySplit(message.Content) is not { } parts) return;

        var attachments = message.Attachments is { Count: > 0 } existing
            ? new ObservableCollection<ImageAttachmentItem>(existing)
            : new ObservableCollection<ImageAttachmentItem>();
        foreach (var document in parts.Documents)
        {
            bool reference = document.ReferencePath is { Length: > 0 };
            attachments.Add(new ImageAttachmentItem
            {
                FileName = reference
                    ? System.IO.Path.GetFileName(document.FileName)
                    : document.FileName,
                ByteSize = document.ApproximateBytes ?? System.Text.Encoding.UTF8.GetByteCount(document.Content),
                IsImage = false,
                MimeType = "text/plain",
                TextContent = reference ? null : document.Content,
                ReferencePath = document.ReferencePath,
            });
        }

        message.Content = parts.Text;
        message.Attachments = attachments;
    }
}
