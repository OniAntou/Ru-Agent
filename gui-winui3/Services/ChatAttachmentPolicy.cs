namespace RuApp.Services;

/// <summary>
/// Pure attachment policy extracted from ChatView (slice 1).
/// No UI, no I/O. Business logic unchanged.
/// </summary>
internal static class ChatAttachmentPolicy
{
    internal const int MaxImageAttachmentBytes = ImageAttachmentResizer.DefaultMaxInputBytes;
    internal const int MaxTextAttachmentBytes = 5 * 1024 * 1024;
    internal const int MaxImageCount = 4;

    /// <summary>
    /// An office document is read by <see cref="OfficeDocumentText"/> rather than as
    /// text, but the extracted text still travels in a prompt, so it is capped like one.
    /// </summary>
    internal const int MaxOfficeDocumentBytes = 20 * 1024 * 1024;

    /// <summary>
    /// The line shown when a turn carries too many images. Composed from
    /// <see cref="MaxImageCount"/> so the number the user reads cannot disagree with the
    /// number the check uses.
    /// </summary>
    internal static string TooManyImagesStatus =>
        $"Maximum {MaxImageCount} images per turn; please remove some images before sending.";

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
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
    }

    internal static bool IsTextOrCodeFile(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".md" or ".markdown" or ".json" or ".xml" or ".yaml" or ".yml" or ".csv" or ".tsv"
            or ".cs" or ".py" or ".js" or ".ts" or ".jsx" or ".tsx" or ".html" or ".htm" or ".css" or ".scss"
            or ".sql" or ".sh" or ".bat" or ".cmd" or ".ps1" or ".rs" or ".go" or ".cpp" or ".c" or ".h" or ".hpp"
            or ".java" or ".kt" or ".swift" or ".dart" or ".php" or ".rb" or ".lua" or ".ini" or ".cfg" or ".conf"
            or ".log" or ".env" or ".toml" or ".proto" or ".graphql" => true,
            _ => false
        };
    }

    /// <summary>
    /// True for a zip-of-XML office document. These are not text files: reading the raw
    /// bytes as UTF-8 would put binary noise in the prompt, so the text has to be
    /// extracted from the OOXML parts first.
    /// </summary>
    internal static bool IsOfficeDocumentFile(string name) => RuAgent.Client.OfficeDocumentText.IsSupported(name);

    internal static string MimeForExtension(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
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
}
