namespace RuApp.Services;

/// <summary>
/// Pure image/text/code attachment classification and bounded reads shared by
/// picker, clipboard and drag-and-drop intake.
/// </summary>
internal static class ChatAttachmentPolicy
{
    internal const int MaxImageAttachmentBytes = ImageAttachmentResizer.DefaultMaxInputBytes;
    internal const int MaxTextAttachmentBytes = 5 * 1024 * 1024;
    internal const int MaxImageCount = 4;

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

    internal static string DecodeText(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var stream = new System.IO.MemoryStream(bytes, writable: false);
        using var reader = new System.IO.StreamReader(
            stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

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
