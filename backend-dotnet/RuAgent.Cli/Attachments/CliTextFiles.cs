using System.Text;
using RuAgent.Cli.Images;

namespace RuAgent.Cli.Attachments;

/// <summary>
/// Loads one pending text or office file: bounded read, UTF-8 decode for text/code and
/// OOXML text extraction for .docx/.pptx/.xlsx. Failures come back as bounded one-line
/// reasons; nothing throws for bad input.
/// </summary>
public static class CliTextFiles
{
    public const int MaxOfficeDocumentBytes = 20 * 1024 * 1024;

    public static bool IsOfficeDocument(string name) => OfficeDocumentText.IsSupported(name);

    public static bool IsSupported(string name) =>
        CliImagePolicy.IsTextOrCodeFile(name) || OfficeDocumentText.IsSupported(name);

    public static async Task<(CliTextAttachment? Attachment, string? Error)> LoadAsync(
        string path,
        string workspace,
        CancellationToken cancellationToken)
    {
        string resolved;
        try
        {
            resolved = CliImageFiles.ResolvePath(path, workspace);
        }
        catch (Exception ex)
        {
            return (null, $"invalid_attachment_path: {CliErrorText.ShortMessage(ex.Message)}");
        }

        var name = Path.GetFileName(resolved);
        if (name.Length == 0)
        {
            return (null, "invalid_attachment_path: the path has no file name");
        }

        if (!IsSupported(resolved))
        {
            return (null, $"unsupported_attachment: {name} is not an image, text or office file");
        }

        if (!File.Exists(resolved))
        {
            return (null, $"attachment_not_found: {name}");
        }

        long length;
        try
        {
            length = new FileInfo(resolved).Length;
        }
        catch (Exception ex)
        {
            return (null, $"attachment_unreadable: {name} - {CliErrorText.ShortMessage(ex.Message)}");
        }

        var isOffice = OfficeDocumentText.IsSupported(resolved);
        var cap = isOffice ? MaxOfficeDocumentBytes : CliImagePolicy.MaxTextAttachmentBytes;
        if (length > cap)
        {
            return (null,
                $"attachment_too_large: {name} is {CliImageAttachment.FormatBytes(length)} (limit {CliImageAttachment.FormatBytes(cap)})");
        }

        byte[] bytes;
        try
        {
            await using var stream = File.OpenRead(resolved);
            var bounded = await CliImagePolicy.ReadBoundedBytesAsync(stream, cap).ConfigureAwait(false);
            if (bounded is null)
            {
                return (null, $"attachment_too_large: {name} grew past the read cap while being read");
            }

            bytes = bounded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"attachment_unreadable: {name} - {CliErrorText.ShortMessage(ex.Message)}");
        }

        return LoadBytes(name, bytes, length);
    }

    internal static (CliTextAttachment? Attachment, string? Error) LoadBytes(string name, byte[] bytes, long sourceLength)
    {
        var isOffice = OfficeDocumentText.IsSupported(name);
        if (!isOffice)
        {
            return (new CliTextAttachment(name, DecodeText(bytes), sourceLength), null);
        }

        var extraction = OfficeDocumentText.Extract(bytes);
        return extraction.Result switch
        {
            OfficeDocumentText.Result.Ok => (new CliTextAttachment(name, extraction.Text, sourceLength), null),
            OfficeDocumentText.Result.NoContent => (null, $"empty_document: {name} has no readable text"),
            OfficeDocumentText.Result.TooLarge => (null, $"document_too_large: {name} exceeds the extraction limits"),
            _ => (null, $"invalid_document: {name} is not a readable office file"),
        };
    }

    /// <summary>UTF-8 with BOM detection; an office-style mojibake read never happens here.</summary>
    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
