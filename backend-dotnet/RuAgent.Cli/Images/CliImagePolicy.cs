namespace RuAgent.Cli.Images;

/// <summary>
/// Pure attachment policy ported from the GUI (<c>ChatAttachmentPolicy</c>): caps, file
/// classification and bounded reads. No UI, no filesystem access of its own.
/// </summary>
public static class CliImagePolicy
{
    public const int MaxImageCount = RuAgent.Protocol.Harness.AgentRunLimits.MaxImages;

    /// <summary>
    /// Largest source file one image attachment may carry. Only the client read and
    /// decode are bounded by this: the decoded/encoded wire image keeps
    /// <see cref="MaxDecodedImageBytes"/> and the 2048 px longest side.
    /// </summary>
    public const int MaxImageAttachmentBytes = 100 * 1024 * 1024;
    public const int MaxTextAttachmentBytes = 5 * 1024 * 1024;

    /// <summary>Backend cap on one decoded image, mirrored so the client refuses early.</summary>
    public const int MaxDecodedImageBytes = (int)RuAgent.Protocol.Harness.AgentRunLimits.MaxImageBytes;

    /// <summary>Backend cap on the decoded bytes of one turn's images, mirrored.</summary>
    public const int MaxTotalImageBytes = (int)RuAgent.Protocol.Harness.AgentRunLimits.MaxTotalImageBytes;

    /// <summary>Backend cap on one data URL, mirrored so the client refuses before POSTing.</summary>
    public const int MaxDataUrlChars = RuAgent.Protocol.Harness.AgentRunLimits.MaxImageDataUrlChars;

    /// <summary>
    /// The line shown when a turn carries too many images. Composed from
    /// <see cref="MaxImageCount"/> so the number the user reads cannot disagree with the
    /// number the check uses.
    /// </summary>
    public static string TooManyImagesStatus =>
        $"Maximum {MaxImageCount} images per turn; please remove some images before sending.";

    public static async Task<byte[]?> ReadBoundedBytesAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Length > maximumBytes - read)
            {
                return null;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    public static bool IsImageFile(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        // GIF and TIFF decode through GDI+ and are re-encoded to a wire format, as BMP is.
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff";
    }

    public static bool IsTextOrCodeFile(string name) =>
        RuAgent.Client.Attachments.AttachmentFileTypes.IsTextOrCodeFile(name);

    public static string MimeForExtension(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            // BMP has no wire form: the Host and the provider adapters accept only
            // png/jpeg/webp, so this is the type the bytes are normalized to, not
            // the type the file carries.
            _ => "image/jpeg",
        };
    }

    /// <summary>
    /// Decoded size of a base64 payload without allocating the bytes: every four
    /// characters carry three bytes minus one byte per padding character.
    /// </summary>
    public static long EstimateDecodedBytes(string base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return 0;
        }

        var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2
            : base64.EndsWith("=", StringComparison.Ordinal) ? 1
            : 0;
        return ((long)base64.Length * 3 / 4) - padding;
    }
}
