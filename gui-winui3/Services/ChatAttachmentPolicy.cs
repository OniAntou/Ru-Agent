namespace RuApp.Services;

/// <summary>
/// Pure image/text/code attachment classification and bounded reads shared by
/// picker, clipboard and drag-and-drop intake.
/// </summary>
internal static class ChatAttachmentPolicy
{
    internal const int MaxImageAttachmentBytes = ImageAttachmentResizer.DefaultMaxInputBytes;
    internal const int MaxTextAttachmentBytes = 5 * 1024 * 1024;
    internal const int MaxDocumentAttachmentBytes = 16 * 1024 * 1024;
    internal const int MaxImageCount = RuAgent.Protocol.Harness.AgentRunLimits.MaxImages;

    /// <summary>
    /// The line shown when a turn carries too many images. Composed from
    /// <see cref="MaxImageCount"/> so the number the user reads cannot disagree with the
    /// number the check uses.
    /// </summary>
    internal static string TooManyImagesStatus =>
        $"Maximum {MaxImageCount} images per turn; please remove some images before sending.";

    /// <summary>
    /// The images, in order, that one turn may carry: at most <see cref="MaxImageCount"/>
    /// and 20 MiB decoded together, as the Host checks them. Used when steers the run never
    /// read become one turn, so the turn is never refused for carrying too many.
    /// </summary>
    internal static List<RuApp.Models.ImageAttachmentItem> TakeImagesWithinTurnBound(
        IEnumerable<RuApp.Models.ImageAttachmentItem> images)
    {
        var kept = new List<RuApp.Models.ImageAttachmentItem>();
        long totalBytes = 0;
        foreach (var image in images)
        {
            if (kept.Count >= MaxImageCount) break;
            long decoded = (image.Base64Data?.Length ?? 0) * 3L / 4;
            if (totalBytes + decoded > RuAgent.Protocol.Harness.AgentRunLimits.MaxTotalImageBytes) continue;
            kept.Add(image);
            totalBytes += decoded;
        }

        return kept;
    }

    internal static async Task<byte[]?> ReadBoundedBytesAsync(System.IO.Stream stream, int maximumBytes)
    {
        using var output = new System.IO.MemoryStream(System.Math.Min(maximumBytes, 81920));
        var buffer = new byte[System.Math.Min(maximumBytes, 81920)];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (output.Length > maximumBytes - read)
            {
                return null;
            }

            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
        }

        return output.ToArray();
    }

    internal static bool IsImageFile(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff";
    }

    internal static bool IsDocumentFile(string name) => AttachmentDocumentExtractor.IsSupportedDocument(name);

    /// <summary>
    /// Whether intake must first ask the Host for the folderless chat's scratch workspace.
    /// Images keep their image path; every other attached file can be staged there by path.
    /// </summary>
    internal static bool NeedsScratchWorkspace(string name, string? workspace) =>
        string.IsNullOrWhiteSpace(workspace) && !IsImageFile(name);

    internal static bool IsTextOrCodeFile(string name) =>
        RuAgent.Client.Attachments.AttachmentFileTypes.IsTextOrCodeFile(name);

    /// <summary>
    /// The file as prompt text, or null when it is not text: invalid UTF-8 without a
    /// byte-order mark, or a NUL byte (<see cref="RuAgent.Client.Attachments.TextAttachmentDecoder"/>,
    /// shared with the terminal). A binary file renamed to .txt used to be inlined as U+FFFD.
    /// </summary>
    internal static string? DecodeText(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return RuAgent.Client.Attachments.TextAttachmentDecoder.TryDecode(bytes, out var text, out _) ? text : null;
    }

    internal static string MimeForExtension(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            // BMP/GIF/TIFF have no wire form: the Host and the provider adapters accept only
            // png/jpeg/webp, so this is the type the bytes are normalized to, not
            // the type the file carries.
            _ => "image/jpeg",
        };
    }
}
