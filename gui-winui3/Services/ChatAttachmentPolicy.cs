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
        return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif" or ".tif" or ".tiff";
    }

    internal static bool IsDocumentFile(string name) => AttachmentDocumentExtractor.IsSupportedDocument(name);

    internal static bool IsTextOrCodeFile(string name)
    {
        string ext = System.IO.Path.GetExtension(name).ToLowerInvariant();
        if (ext.Length == 0)
        {
            return name.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Makefile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Rakefile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Gemfile", StringComparison.OrdinalIgnoreCase)
                || name.Equals("LICENSE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("NOTICE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("README", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CHANGELOG", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".gitignore", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".dockerignore", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".nvmrc", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".env", StringComparison.OrdinalIgnoreCase);
        }

        return ext switch
        {
            ".txt" or ".md" or ".markdown" or ".json" or ".jsonc" or ".xml" or ".yaml" or ".yml"
            or ".csv" or ".tsv" or ".cs" or ".py" or ".js" or ".ts" or ".jsx" or ".tsx" or ".html" or ".htm"
            or ".css" or ".scss" or ".sass" or ".less" or ".sql" or ".sh" or ".bash" or ".zsh" or ".bat"
            or ".cmd" or ".ps1" or ".psm1" or ".psd1" or ".rs" or ".go" or ".cpp" or ".cc" or ".c" or ".h"
            or ".hpp" or ".java" or ".kt" or ".kts" or ".swift" or ".dart" or ".php" or ".rb" or ".lua"
            or ".ini" or ".cfg" or ".conf" or ".log" or ".env" or ".toml" or ".proto" or ".graphql"
            or ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" or ".razor" or ".cshtml" or ".vue"
            or ".svelte" or ".gradle" or ".groovy" or ".m" or ".mm" or ".scala" or ".sc" or ".sol" or ".asm"
            or ".s" or ".fs" or ".fsx" or ".vb" or ".r" or ".pl" or ".ex" or ".exs" or ".erl" or ".hrl"
            or ".clj" or ".cljs" or ".hs" or ".lhs" or ".ml" or ".mli" or ".tex" or ".rst" or ".adoc"
            or ".properties" or ".editorconfig" or ".gitignore" or ".gitattributes" or ".dockerignore"
            => true,
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
            // BMP/GIF/TIFF have no wire form: the Host and the provider adapters accept only
            // png/jpeg/webp, so this is the type the bytes are normalized to, not
            // the type the file carries.
            _ => "image/jpeg",
        };
    }
}
