namespace RuAgent.Cli.Images;

/// <summary>
/// Pure attachment policy ported from the GUI (<c>ChatAttachmentPolicy</c>): caps, file
/// classification and bounded reads. No UI, no filesystem access of its own.
/// </summary>
public static class CliImagePolicy
{
    public const int MaxImageCount = 4;
    public const int MaxImageAttachmentBytes = 10 * 1024 * 1024;
    public const int MaxTextAttachmentBytes = 5 * 1024 * 1024;

    /// <summary>Backend cap on one decoded image, mirrored so the client refuses early.</summary>
    public const int MaxDecodedImageBytes = 4 * 1024 * 1024;

    /// <summary>Backend cap on the decoded bytes of one turn's images, mirrored.</summary>
    public const int MaxTotalImageBytes = 20 * 1024 * 1024;

    /// <summary>Backend cap on one data URL, mirrored so the client refuses before POSTing.</summary>
    public const int MaxDataUrlChars = 5_600_000;

    /// <summary>
    /// The line shown when a turn carries too many images. Composed from
    /// <see cref="MaxImageCount"/> so the number the user reads cannot disagree with the
    /// number the check uses.
    /// </summary>
    public static string TooManyImagesStatus =>
        $"Maximum {MaxImageCount} images per turn; please remove some images before sending.";

    public static async Task<byte[]?> ReadBoundedBytesAsync(Stream stream, int maximumBytes)
    {
        using var output = new MemoryStream(Math.Min(maximumBytes, 81920));
        var buffer = new byte[Math.Min(maximumBytes, 81920)];
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

    public static bool IsImageFile(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
    }

    public static bool IsTextOrCodeFile(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".txt" or ".md" or ".markdown" or ".json" or ".xml" or ".yaml" or ".yml" or ".csv" or ".tsv"
            or ".cs" or ".py" or ".js" or ".ts" or ".jsx" or ".tsx" or ".html" or ".htm" or ".css" or ".scss"
            or ".sql" or ".sh" or ".bat" or ".cmd" or ".ps1" or ".rs" or ".go" or ".cpp" or ".c" or ".h" or ".hpp"
            or ".java" or ".kt" or ".swift" or ".dart" or ".php" or ".rb" or ".lua" or ".ini" or ".cfg" or ".conf"
            or ".log" or ".env" or ".toml" or ".proto" or ".graphql" => true,
            _ => false,
        };
    }

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
