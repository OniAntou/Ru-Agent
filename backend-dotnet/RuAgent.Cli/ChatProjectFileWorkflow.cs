using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32.SafeHandles;
using RuAgent.Cli.Attachments;
using RuAgent.Cli.Images;
using RuAgent.Cli.Tui;

namespace RuAgent.Cli;

internal sealed record ChatProjectFileChoice(string Path, string RelativePath)
{
    public string Display => RelativePath;
}

internal static class ChatProjectFileWorkflow
{
    public const int MaxListedFiles = 50;
    public const int MaxPendingAttachments = 8;
    public const long MaxPendingAttachmentBytes = 32L * 1024 * 1024;
    private const int MaxScannedEntries = 5_000;
    private const int MaxQueryChars = 128;
    private const int MaxDisplayPathChars = 240;
    private const int MaxFinalPathChars = 32_768;
    private const int FileStreamBufferBytes = 64 * 1024;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(2);
    private static readonly HashSet<string> SkippedDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".ru", ".venv", "venv", "node_modules", "bin", "obj", "packages", "artifacts",
        "target", "dist", "build", "coverage", ".cache", ".ssh", "secrets", "credentials",
    };
    private static readonly HashSet<string> SensitiveFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".env", ".env.local", ".npmrc", ".netrc", "id_rsa", "id_ed25519", "credentials", "secrets",
    };

    public static IReadOnlyList<ChatProjectFileChoice> FindCandidates(
        string workspace,
        string? query,
        CancellationToken cancellationToken)
    {
        var workspacePath = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (HasReparseAncestor(workspacePath))
        {
            throw new UnauthorizedAccessException("workspace path crosses a reparse point");
        }

        using var rootHandle = OpenDirectory(workspacePath);
        var root = GetFinalPath(rootHandle);
        var normalizedQuery = query?.Trim();
        if (normalizedQuery?.Length > MaxQueryChars)
        {
            normalizedQuery = normalizedQuery[..MaxQueryChars];
        }

        var pending = new Queue<string>();
        var choices = new List<ChatProjectFileChoice>(MaxListedFiles);
        var timer = Stopwatch.StartNew();
        var scanned = 0;
        pending.Enqueue(root);
        bool CanContinue() => scanned < MaxScannedEntries && timer.Elapsed < ScanTimeout;
        while (pending.Count > 0
            && CanContinue()
            && choices.Count < MaxListedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Dequeue();
            foreach (var child in Enumerate(directory, directories: true, CanContinue))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanContinue())
                {
                    break;
                }

                if (++scanned >= MaxScannedEntries)
                {
                    break;
                }

                if (!IsSafeDirectory(child))
                {
                    continue;
                }

                pending.Enqueue(child);
            }

            foreach (var path in Enumerate(directory, directories: false, CanContinue))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!CanContinue() || ++scanned >= MaxScannedEntries || choices.Count >= MaxListedFiles)
                {
                    break;
                }

                if (!IsSafeCandidate(path))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, path);
                if (!string.IsNullOrWhiteSpace(normalizedQuery)
                    && !relative.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                choices.Add(new ChatProjectFileChoice(path, ChatDisplayText.Sanitize(relative, MaxDisplayPathChars)));
            }
        }

        return choices;
    }

    public static ChoiceList ToChoiceList(IReadOnlyList<ChatProjectFileChoice> choices) =>
        new("project file", choices.Select((choice, index) => new ChoiceItem(
            choice.Display,
            choice.RelativePath,
            index.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray(),
            footer: "Enter attaches · Esc cancels · limited results; refine search");

    public static async Task<(CliImageAttachment? Image, CliTextAttachment? Text, string? Error)> LoadSelectedAsync(
        string selectedPath,
        string workspace,
        CancellationToken cancellationToken)
    {
        if (!TryGetBoundedPath(selectedPath, workspace, out var path, out var root))
        {
            return (null, null, "project_file_denied: selected path is outside the current workspace");
        }

        var name = Path.GetFileName(path);
        var safeName = ChatDisplayText.Sanitize(name, 120);
        if (!IsSafeCandidate(path) || HasProtectedOrReparseParent(path, root))
        {
            return (null, null, "project_file_denied: selected file is private, unsupported, or crosses a reparse point");
        }

        var isImage = CliImagePolicy.IsImageFile(path);
        var cap = isImage ? CliImagePolicy.MaxImageAttachmentBytes
            : CliTextFiles.IsOfficeDocument(path) ? CliTextFiles.MaxOfficeDocumentBytes
            : CliImagePolicy.MaxTextAttachmentBytes;

        try
        {
            await using var handleStream = OpenContainedFile(path, root);
            var length = handleStream.Length;
            if (length > cap)
            {
                return (null, null, $"project_file_too_large: {name} exceeds the {CliImageAttachment.FormatBytes(cap)} limit");
            }

            var bytes = await CliImagePolicy.ReadBoundedBytesAsync(handleStream, cap, cancellationToken).ConfigureAwait(false);
            if (bytes is null)
            {
                return (null, null, $"project_file_too_large: {name} grew beyond its read limit");
            }

            if (HasProtectedOrReparseParent(path, root))
            {
                return (null, null, "project_file_denied: a parent path changed during selection");
            }

            if (isImage)
            {
                var loaded = CliImageFiles.LoadBytes(name, bytes);
                return (loaded.Attachment, null, loaded.Error);
            }

            var text = CliTextFiles.LoadBytes(name, bytes, length);
            return (null, text.Attachment, text.Error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return (null, null, "project_file_denied: the opened file identity is outside the selected project boundary");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            return (null, null, $"project_file_unreadable: {safeName} - {ChatDisplayText.Sanitize(CliErrorText.ShortMessage(ex.Message), 160)}");
        }
    }

    private static FileStream OpenContainedFile(string path, string workspaceRoot)
    {
        using var rootHandle = OpenDirectory(workspaceRoot);
        var fileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var rootFinal = GetFinalPath(rootHandle);
            var fileFinal = GetFinalPath(fileHandle);
            if (!string.Equals(rootFinal, workspaceRoot, StringComparison.Ordinal)
                || !string.Equals(fileFinal, path, StringComparison.Ordinal)
                || !IsWithinRootExact(fileFinal, rootFinal)
                || !GetFileInformationByHandle(fileHandle, out var information)
                || information.NumberOfLinks != 1)
            {
                throw new UnauthorizedAccessException("opened file identity is outside the selected project boundary");
            }

            return new FileStream(fileHandle, FileAccess.Read, FileStreamBufferBytes, isAsync: true);
        }
        catch
        {
            fileHandle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenDirectory(string path)
    {
        var handle = CreateFileW(path, 0, ShareRead | ShareWrite, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new IOException("workspace directory could not be opened");
        }

        return handle;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(MaxFinalPathChars);
        var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity)
        {
            throw new IOException("opened path could not be resolved");
        }

        var path = buffer.ToString();
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            return "\\\\" + path[8..];
        }

        return path.StartsWith("\\\\?\\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static bool TryGetBoundedPath(string candidate, string workspace, out string path, out string root)
    {
        path = string.Empty;
        root = string.Empty;
        try
        {
            var workspacePath = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (HasReparseAncestor(workspacePath))
            {
                return false;
            }

            using var rootHandle = OpenDirectory(workspacePath);
            root = GetFinalPath(rootHandle);
            path = Path.GetFullPath(candidate);
            return IsWithinRoot(path, root);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasReparseAncestor(string path)
    {
        try
        {
            var current = path;
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
                {
                    return false;
                }

                current = parent;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison)
            || path.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || path.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }

    private static bool IsWithinRootExact(string path, string root) =>
        path.Equals(root, StringComparison.Ordinal)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || path.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

    private static bool HasProtectedOrReparseParent(string path, string root)
    {
        try
        {
            var current = path;
            while (current.Length >= root.Length)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                if (string.Equals(current, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return false;
                }

                var name = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (name.StartsWith(".", StringComparison.Ordinal) || SkippedDirectoryNames.Contains(name))
                {
                    return true;
                }

                current = Path.GetDirectoryName(current) ?? string.Empty;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return true;
        }
    }

    private static bool IsSafeDirectory(string path)
    {
        try
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return !name.StartsWith(".", StringComparison.Ordinal)
                && !SkippedDirectoryNames.Contains(name)
                && (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSafeCandidate(string path)
    {
        try
        {
            var name = Path.GetFileName(path);
            var attributes = File.GetAttributes(path);
            var ext = Path.GetExtension(name);
            return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory)) == 0
                && !IsSensitiveFileName(name)
                && (CliImagePolicy.IsImageFile(name) || CliTextFiles.IsSupported(name))
                && ext.Length <= 16;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsSensitiveFileName(string name) =>
        SensitiveFileNames.Contains(name)
        || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".key", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".p12", StringComparison.OrdinalIgnoreCase)
        || name.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || name.Contains("password", StringComparison.OrdinalIgnoreCase)
        || name.Contains("secret", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> Enumerate(string directory, bool directories, Func<bool> canContinue)
    {
        IEnumerator<string>? enumerator = null;
        var failed = false;
        try
        {
            enumerator = (directories ? Directory.EnumerateDirectories(directory) : Directory.EnumerateFiles(directory)).GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            failed = true;
        }

        if (failed || enumerator is null)
        {
            yield break;
        }

        using (enumerator)
        {
            while (canContinue())
            {
                bool hasNext;
                string? path;
                failed = false;
                try
                {
                    hasNext = enumerator.MoveNext();
                    path = hasNext ? enumerator.Current : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    failed = true;
                    hasNext = false;
                    path = null;
                }

                if (failed || !hasNext || path is null)
                {
                    yield break;
                }

                yield return path;
            }
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint pathLength, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
