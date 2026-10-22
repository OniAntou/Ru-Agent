using System.Runtime.InteropServices;
using System.Text;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace RuApp.Services;

public enum FilePreviewKind { Text, Markdown, Image, Pdf, Office }

/// <summary>User-initiated, read-only presentation input. Never exposed as a model execution path.</summary>
public static class FilePreviewSource
{
    public const long MaximumBytes = 64 * 1024 * 1024;
    public const long MaximumTextBytes = 1024 * 1024;
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".pptx", ".xlsx", ".doc", ".ppt", ".xls", ".rtf", ".odt", ".odp", ".ods" };
    private static readonly HashSet<string> Text = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".cs", ".py", ".js", ".jsx", ".ts", ".tsx", ".json", ".xml", ".xaml", ".html", ".css", ".csv", ".log", ".yml", ".yaml", ".toml", ".ini", ".ps1", ".sh", ".sql", ".svg", ".md", ".markdown" };

    public static FilePreviewKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return FilePreviewKind.Image;
        if (Documents.Contains(ext)) return FilePreviewKind.Office;
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return FilePreviewKind.Pdf;
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)) return FilePreviewKind.Markdown;
        if (Text.Contains(ext)) return FilePreviewKind.Text;
        throw new NotSupportedException("preview_format_unsupported: No preview is available for this file type.");
    }

    public static FileStream Open(string workspace, string path)
    {
        string root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("preview_outside_workspace");
        for (string? current = full; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("preview_link_not_allowed");
            string name = Path.GetFileName(current);
            if (current.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && FileTreeLister.IsSkipped(name, Directory.Exists(current))) throw new UnauthorizedAccessException("preview_hidden_path");
        }
        var kind = Classify(full);
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        try
        {
            var resolved = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, resolved, (uint)resolved.Capacity, 0);
            if (length == 0 || length >= resolved.Capacity) throw new IOException("preview_path_unavailable");
            string actual = resolved.ToString();
            if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
            else if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
            if (!string.Equals(actual, full, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("preview_path_changed");
            long limit = kind is FilePreviewKind.Text or FilePreviewKind.Markdown ? MaximumTextBytes : MaximumBytes;
            if (stream.Length > limit) throw new IOException($"preview_too_large: Limit is {limit / (1024 * 1024)} MiB.");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public static void ValidateOfficePackage(Stream source, string path)
    {
        string? part = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".docx" => "word/document.xml",
            ".pptx" => "ppt/presentation.xml",
            ".xlsx" => "xl/workbook.xml",
            _ => null,
        };
        if (part is null) return;
        try
        {
            using var package = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            if (package.Entries.Count > 8192 || package.GetEntry("[Content_Types].xml") is null || package.GetEntry(part) is null)
                throw new InvalidDataException();
        }
        catch (InvalidDataException)
        {
            throw new IOException("preview_invalid_document: This is not a valid Office document package.");
        }
        finally { source.Position = 0; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
