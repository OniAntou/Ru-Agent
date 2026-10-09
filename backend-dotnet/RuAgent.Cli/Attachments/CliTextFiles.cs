using System.Text;
using RuAgent.Cli.Images;

namespace RuAgent.Cli.Attachments;

/// <summary>
/// Loads one pending text/code file with a bounded read and UTF-8 BOM detection.
/// Unsupported formats are refused before decoding.
/// </summary>
public static class CliTextFiles
{
    public static bool IsSupported(string name) =>
        CliImagePolicy.IsTextOrCodeFile(name);

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
            return (null, $"unsupported_attachment: {name} is not a supported text/code file");
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

        var cap = CliImagePolicy.MaxTextAttachmentBytes;
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
        if (!IsSupported(name))
        {
            return (null, $"unsupported_attachment: {name} is not a supported text/code file");
        }

        return (new CliTextAttachment(name, DecodeText(bytes), sourceLength), null);
    }

    /// <summary>UTF-8 with BOM detection; the BOM is encoding, not prompt text.</summary>
    private static string DecodeText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
