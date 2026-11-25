using System.Runtime.InteropServices;
using System.Text;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;
using RuApp.Controls;

namespace RuApp.Services;

public enum FilePreviewKind { Text, Markdown, Image, Pdf, Office }

/// <summary>
/// What the preview header may say about a file: its length and last write time,
/// both read from the handle that was validated for the read itself. The time is
/// null when the handle cannot report it, rather than a guessed value.
/// </summary>
public readonly record struct PreviewFileFacts(long Length, DateTime? LastWriteTimeUtc);

/// <summary>User-initiated, read-only presentation input. Never exposed as a model execution path.</summary>
public static class FilePreviewSource
{
    public const long MaximumBytes = 64 * 1024 * 1024;
    public const long MaximumTextBytes = 1024 * 1024;
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };
    private static readonly HashSet<string> Documents = new(StringComparer.OrdinalIgnoreCase) { ".docx", ".pptx", ".xlsx", ".doc", ".ppt", ".xls", ".rtf", ".odt", ".odp", ".ods" };

    /// <summary>Prose and data whose extensions no highlighter language covers.</summary>
    private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".txt", ".csv", ".log", ".text" };

    /// <summary>Well-known files that carry their type in the name instead of an extension.</summary>
    private static readonly HashSet<string> PlainTextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Dockerfile", "Containerfile", "Makefile", "GNUmakefile", "Justfile",
        "Gemfile", "Rakefile", "Procfile", "Vagrantfile", "Brewfile",
        "LICENSE", "LICENCE", "COPYING", "NOTICE", "AUTHORS", "CONTRIBUTORS",
        "README", "CHANGELOG", "CHANGES", "TODO", "VERSION",
        ".gitignore", ".gitattributes", ".gitmodules", ".dockerignore", ".editorconfig", ".mailmap",
        ".prettierrc", ".babelrc", ".eslintrc", ".nvmrc", ".node-version", ".python-version",
        "go.mod", "go.sum", "Cargo.lock",
    };

    /// <summary>An SVG is read as text (its size cap and checks) and shown as a picture drawn by the sandboxed renderer.</summary>
    public static bool IsSvg(string? path) =>
        string.Equals(Path.GetExtension(path), ".svg", StringComparison.OrdinalIgnoreCase);

    public static FilePreviewKind Classify(string path)
    {
        var ext = Path.GetExtension(path);
        if (Images.Contains(ext)) return FilePreviewKind.Image;
        if (Documents.Contains(ext)) return FilePreviewKind.Office;
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return FilePreviewKind.Pdf;
        if (ext.Equals(".md", StringComparison.OrdinalIgnoreCase) || ext.Equals(".markdown", StringComparison.OrdinalIgnoreCase)) return FilePreviewKind.Markdown;
        if (IsTextual(path)) return FilePreviewKind.Text;
        throw new NotSupportedException("preview_format_unsupported: No preview is available for this file type.");
    }

    /// <summary>
    /// Whether the file is shown as text. <see cref="CodeHighlighter"/> owns the one
    /// table of source types, so a language it colors can never be refused here; the
    /// plain set adds prose and the names a project states without an extension.
    /// </summary>
    public static bool IsTextual(string path)
    {
        string name = Path.GetFileName(path);
        return !string.IsNullOrEmpty(name)
            && (PlainTextFileNames.Contains(name)
                || PlainTextExtensions.Contains(Path.GetExtension(name))
                || CodeHighlighter.LanguageForFile(name) is not null);
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

    /// <summary>
    /// The size and last write time of an already validated handle. Reading them from
    /// the handle keeps the header describing the bytes that were actually read.
    /// </summary>
    public static PreviewFileFacts Describe(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        long length = stream.Length;
        if (!GetFileInformationByHandleEx(stream.SafeFileHandle, FileBasicInfoClass, out var info, (uint)Marshal.SizeOf<FileBasicInfo>()))
        {
            return new PreviewFileFacts(length, null);
        }

        DateTime? modified = null;
        if (info.LastWriteTime > 0 && info.LastWriteTime <= MaxFileTimeUtc)
        {
            modified = DateTime.FromFileTimeUtc(info.LastWriteTime);
        }

        return new PreviewFileFacts(length, modified);
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

    private const int FileBasicInfoClass = 0;
    private static readonly long MaxFileTimeUtc = DateTime.MaxValue.ToFileTimeUtc();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileBasicInfo info, uint size);
}
