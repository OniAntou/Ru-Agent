namespace RuApp.Services;

/// <summary>
/// Pure attachment policy extracted from ChatView (slice 1).
/// No UI, no I/O. Business logic unchanged.
/// </summary>
internal static class ChatAttachmentPolicy
{
    internal const int MaxImageAttachmentBytes = 10 * 1024 * 1024;
    internal const int MaxTextAttachmentBytes = 5 * 1024 * 1024;
    internal const int MaxImageCount = 4;

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

    internal static string MimeForExtension(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "image/png"
        };
    }
}
