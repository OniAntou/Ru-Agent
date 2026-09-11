using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RuAgent.Broker.Tools.Paths;

public sealed class CanonicalPathResolver
{
    private readonly string _canonicalRoot;

    public CanonicalPathResolver(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        // Resolve the workspace root to its final canonical form if it exists, otherwise normalize.
        var full = Path.GetFullPath(workspaceRoot);
        if (Directory.Exists(full) || File.Exists(full))
        {
            var final = TryGetFinalPath(full);
            if (!string.IsNullOrWhiteSpace(final))
            {
                full = final;
            }
        }

        if (!Path.IsPathRooted(full))
        {
            throw new ArgumentException("Workspace root must be rooted.", nameof(workspaceRoot));
        }

        _canonicalRoot = TrimTrailing(full);
    }

    public string CanonicalRoot => _canonicalRoot;

    public ResolvedPath Resolve(string inputPath)
    {
        if (inputPath is null)
        {
            throw new ArgumentNullException(nameof(inputPath));
        }

        if (inputPath.Length == 0 || string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Path must not be empty or blank.", nameof(inputPath));
        }

        if (inputPath.Contains('\0'))
        {
            throw new ArgumentException("Path contains null character.", nameof(inputPath));
        }

        if (HasAlternateDataStream(inputPath))
        {
            throw new ArgumentException("Alternate data streams are not allowed.", nameof(inputPath));
        }

        var isAbsolute = Path.IsPathRooted(inputPath);
        string logicalCandidate;
        try
        {
            logicalCandidate = isAbsolute
                ? Path.GetFullPath(inputPath)
                : Path.GetFullPath(Path.Combine(_canonicalRoot, inputPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Invalid path.", nameof(inputPath), ex);
        }

        var canonical = ResolveFinalPath(logicalCandidate);

        PathClassification classification;
        if (ProtectedPathCatalog.IsProtected(canonical))
        {
            classification = PathClassification.Protected;
        }
        else if (IsWithinRoot(canonical, _canonicalRoot))
        {
            classification = PathClassification.InsideWorkspace;
        }
        else
        {
            classification = PathClassification.OutsideWorkspace;
        }

        var hash = ResolvedPath.ComputeHash(canonical);
        return new ResolvedPath(inputPath, canonical, classification, hash, isAbsolute);
    }

    public static bool HasChanged(ResolvedPath previous, ResolvedPath current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        return !string.Equals(previous.CanonicalPath, current.CanonicalPath, StringComparison.OrdinalIgnoreCase)
               || !string.Equals(previous.CanonicalIntentHash, current.CanonicalIntentHash, StringComparison.Ordinal)
               || previous.Classification != current.Classification;
    }

    private string ResolveFinalPath(string path)
    {
        // If path exists, try to get final path directly.
        if (File.Exists(path) || Directory.Exists(path))
        {
            var final = TryGetFinalPath(path);
            if (!string.IsNullOrWhiteSpace(final))
            {
                return Path.GetFullPath(final);
            }

            // Fallback: try to resolve via link target traversal
            var linkResolved = TryResolveLinkTarget(path);
            if (!string.IsNullOrWhiteSpace(linkResolved))
            {
                return Path.GetFullPath(linkResolved);
            }

            return Path.GetFullPath(path);
        }

        // Missing target: find longest existing ancestor, resolve its final, then append suffix.
        var existingAncestor = FindExistingAncestor(path);
        if (existingAncestor is null)
        {
            // No ancestor exists, just normalize logically
            var fallback = TryGetFinalPath(Path.GetPathRoot(path) ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                // fallback to root final
            }

            return Path.GetFullPath(path);
        }

        var finalAncestor = TryGetFinalPath(existingAncestor);
        if (string.IsNullOrWhiteSpace(finalAncestor))
        {
            finalAncestor = TryResolveLinkTarget(existingAncestor);
        }

        if (string.IsNullOrWhiteSpace(finalAncestor))
        {
            finalAncestor = Path.GetFullPath(existingAncestor);
        }
        else
        {
            finalAncestor = Path.GetFullPath(finalAncestor);
        }

        string suffix;
        try
        {
            suffix = Path.GetRelativePath(existingAncestor, path);
            if (suffix == "." )
            {
                return finalAncestor;
            }

            if (suffix.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                suffix.Equals("..", StringComparison.Ordinal) ||
                suffix.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                // GetRelativePath fell back to outside; compute suffix via substring
                suffix = path.Substring(existingAncestor.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch
        {
            suffix = path.Substring(existingAncestor.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (string.IsNullOrEmpty(suffix))
        {
            return finalAncestor;
        }

        var combined = Path.Combine(finalAncestor, suffix);
        // Walk suffix segments for symlink inside suffix? But suffix does not exist, so no symlink.
        // However intermediate ancestors inside suffix may have been missed if we only resolved top ancestor.
        // To handle nested junctions inside suffix that don't exist as ancestor (should not happen because we found longest existing ancestor)
        // suffix is missing, so no further symlink to resolve.
        return Path.GetFullPath(combined);
    }

    private static string? FindExistingAncestor(string path)
    {
        var current = path;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent))
            {
                // Check root
                var root = Path.GetPathRoot(current);
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    return root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                return null;
            }

            if (Directory.Exists(parent) || File.Exists(parent))
            {
                return Path.GetFullPath(parent);
            }

            if (Directory.Exists(current) || File.Exists(current))
            {
                return Path.GetFullPath(current);
            }

            current = parent;
        }

        return null;
    }

    private static string? TryResolveLinkTarget(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (info.LinkTarget is null)
            {
                // Check parent segments for links via walk
                return WalkAndResolveLinks(path);
            }

            var target = info.ResolveLinkTarget(true);
            if (target is not null)
            {
                // Recursively resolve if target itself is link
                return target.FullName;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? WalkAndResolveLinks(string path)
    {
        try
        {
            // Walk each segment from root to leaf to find any link
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return null;
            }

            var relative = Path.GetRelativePath(root, path);
            if (relative == "." || string.IsNullOrWhiteSpace(relative))
            {
                return null;
            }

            var segments = relative.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            var current = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(current))
            {
                current = root;
            }

            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                current = Path.Combine(current, segment);
                FileSystemInfo info;
                if (Directory.Exists(current))
                {
                    info = new DirectoryInfo(current);
                }
                else if (File.Exists(current))
                {
                    info = new FileInfo(current);
                }
                else
                {
                    // Missing segment, stop walk
                    break;
                }

                if (info.LinkTarget is not null)
                {
                    var resolved = info.ResolveLinkTarget(true)?.FullName;
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        // Need to append remaining segments after this link.
                        var remaining = index + 1 < segments.Length
                            ? Path.Combine(segments[(index + 1)..])
                            : string.Empty;
                        if (string.IsNullOrEmpty(remaining))
                        {
                            return resolved;
                        }

                        // If resolved is outside root, keep outside
                        return Path.Combine(resolved, remaining);
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetFinalPath(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // On non-Windows, try link target resolution then GetFullPath
                var linkResolved = TryResolveLinkTarget(path);
                if (!string.IsNullOrWhiteSpace(linkResolved))
                {
                    return linkResolved;
                }

                return null;
            }

            // Windows: use GetFinalPathNameByHandle
            const uint GENERIC_READ = 0x80000000;
            const uint FILE_SHARE_READ = 0x00000001;
            const uint FILE_SHARE_WRITE = 0x00000002;
            const uint FILE_SHARE_DELETE = 0x00000004;
            const uint OPEN_EXISTING = 3;
            const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
            const int FILE_NAME_NORMALIZED = 0x0;

            using var handle = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return null;
            }

            var sb = new StringBuilder(1024);
            var result = GetFinalPathNameByHandle(handle, sb, sb.Capacity, FILE_NAME_NORMALIZED);
            if (result == 0 || result > sb.Capacity)
            {
                if (result > sb.Capacity)
                {
                    sb = new StringBuilder(checked((int)result));
                    result = GetFinalPathNameByHandle(handle, sb, sb.Capacity, FILE_NAME_NORMALIZED);
                    if (result == 0)
                    {
                        return null;
                    }
                }
                else
                {
                    return null;
                }
            }

            var final = sb.ToString();
            if (final.StartsWith(@"\\?\", StringComparison.Ordinal))
            {
                final = final.Substring(4);
                if (final.StartsWith("UNC\\", StringComparison.Ordinal))
                {
                    final = @"\\" + final.Substring(4);
                }
            }

            return Path.GetFullPath(final);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWithinRoot(string candidate, string root)
    {
        var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSep = normalizedRoot + Path.DirectorySeparatorChar;
        return normalizedCandidate.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimTrailing(string path)
    {
        if (path.Length > 3 && (path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)))
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        return path;
    }

    private static bool HasAlternateDataStream(string path)
    {
        var colonIndex = path.IndexOf(':');
        if (colonIndex < 0)
        {
            return false;
        }

        if (colonIndex == 1 && char.IsAsciiLetter(path[0]))
        {
            return path.IndexOf(':', 2) >= 0;
        }

        if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            && colonIndex == 5
            && char.IsAsciiLetter(path[4]))
        {
            return path.IndexOf(':', 6) >= 0;
        }

        return true;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath, int cchFilePath, int dwFlags);
}
