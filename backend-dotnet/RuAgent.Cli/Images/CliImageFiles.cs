namespace RuAgent.Cli.Images;

/// <summary>
/// Loads one pending image from a path through the validate-resize-encode pipeline the GUI
/// uses: bounded read, wire-format normalization, cap checks and base64 for the run
/// request. Failures come back as bounded one-line reasons; nothing throws for bad input.
/// </summary>
public static class CliImageFiles
{
    /// <summary>Resolves a prompt or clipboard path against the workspace, quotes trimmed.</summary>
    public static string ResolvePath(string path, string workspace)
    {
        var trimmed = path.Trim().Trim('"', '\'');
        return Path.IsPathRooted(trimmed)
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(trimmed, workspace);
    }

    public static async Task<(CliImageAttachment? Attachment, string? Error)> LoadAsync(
        string path,
        string workspace,
        CancellationToken cancellationToken)
    {
        string resolved;
        try
        {
            resolved = ResolvePath(path, workspace);
        }
        catch (Exception ex)
        {
            return (null, $"invalid_image_path: {CliErrorText.ShortMessage(ex.Message)}");
        }

        var name = Path.GetFileName(resolved);
        if (name.Length == 0)
        {
            return (null, "invalid_image_path: the path has no file name");
        }

        if (!CliImagePolicy.IsImageFile(resolved))
        {
            return (null, $"unsupported_image: {name} is not a png/jpeg/webp/bmp file");
        }

        if (!File.Exists(resolved))
        {
            return (null, $"image_not_found: {name}");
        }

        long length;
        try
        {
            length = new FileInfo(resolved).Length;
        }
        catch (Exception ex)
        {
            return (null, $"image_unreadable: {name} - {CliErrorText.ShortMessage(ex.Message)}");
        }

        if (length > CliImagePolicy.MaxImageAttachmentBytes)
        {
            return (null,
                $"image_too_large: {name} is {CliImageAttachment.FormatBytes(length)} (limit {CliImageAttachment.FormatBytes(CliImagePolicy.MaxImageAttachmentBytes)})");
        }

        byte[] bytes;
        try
        {
            await using var stream = File.OpenRead(resolved);
            var bounded = await CliImagePolicy
                .ReadBoundedBytesAsync(stream, CliImagePolicy.MaxImageAttachmentBytes)
                .ConfigureAwait(false);
            if (bounded is null)
            {
                return (null, $"image_too_large: {name} grew past the read cap while being read");
            }

            bytes = bounded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, $"image_unreadable: {name} - {CliErrorText.ShortMessage(ex.Message)}");
        }

        CliResizedImage resized;
        try
        {
            resized = CliImageResizer.ResizeIfNeeded(bytes);
        }
        catch (InvalidDataException ex)
        {
            return (null, $"invalid_image: {name} - {ex.Message}");
        }

        var base64 = Convert.ToBase64String(resized.Bytes);
        if (base64.Length > CliImagePolicy.MaxDataUrlChars)
        {
            return (null,
                $"image_too_large: {name} encodes to {base64.Length} chars (limit {CliImagePolicy.MaxDataUrlChars})");
        }

        return (new CliImageAttachment(
            name,
            resized.MimeType,
            base64,
            resized.Width,
            resized.Height,
            resized.Bytes.LongLength), null);
    }
}
