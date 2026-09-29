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

        // The input check above cannot see a stream name that only appears after
        // link resolution (a link target may carry `file:stream`). Re-check the
        // resolved path so classification never runs on an ADS name.
        if (HasAlternateDataStream(canonical))
        {
            throw new ArgumentException("Alternate data streams are not allowed.", nameof(inputPath));
        }

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

    private const int MaxLinkHops = 32;

    private string ResolveFinalPath(string path)
    {
        // Fast path: the OS final-path API resolves existing links authoritatively.
        var final = TryGetFinalPath(path);
        if (!string.IsNullOrWhiteSpace(final))
        {
            return Path.GetFullPath(final);
        }

        // Managed fallback: walk every segment so links are resolved from their
        // declared targets (including broken links, which File.Exists reports as
        // missing). Anything that cannot be proven is rejected: classification
        // must never run on an unresolved logical name.
        return ResolveByLinkWalk(path, MaxLinkHops);
    }

    private static string ResolveByLinkWalk(string path, int hopsLeft)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(root))
        {
            return Path.GetFullPath(path);
        }

        var relative = Path.GetRelativePath(root, path);
        if (relative == ".")
        {
            return Path.GetFullPath(root);
        }

        var current = root;
        var segments = relative.Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < segments.Length; index++)
        {
            var probe = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(probe);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // Nothing exists from here on, so no link can hide below: the
                // remaining segments are plain names under the resolved parent.
                return Path.GetFullPath(Path.Combine(probe, CombineRemaining(segments, index + 1)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new ArgumentException("Path could not be canonicalized.", ex);
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = probe;
                continue;
            }

            current = ResolveLinkSegment(probe, attributes, hopsLeft);
        }

        return Path.GetFullPath(current);
    }

    private static string ResolveLinkSegment(string linkPath, FileAttributes attributes, int hopsLeft)
    {
        if (hopsLeft <= 0)
        {
            throw new ArgumentException("Path has too many symbolic links to canonicalize.");
        }

        string rawTarget;
        try
        {
            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(linkPath)
                : new FileInfo(linkPath);
            rawTarget = info.LinkTarget
                ?? throw new ArgumentException("Path link target could not be read.");
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ArgumentException("Path could not be canonicalized.", ex);
        }

        var absoluteTarget = Path.IsPathRooted(rawTarget)
            ? Path.GetFullPath(rawTarget)
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(linkPath) ?? string.Empty, rawTarget));

        return ResolveByLinkWalk(absoluteTarget, hopsLeft - 1);
    }

    private static string CombineRemaining(string[] segments, int start)
    {
        return start >= segments.Length ? string.Empty : string.Join(Path.DirectorySeparatorChar, segments[start..]);
    }

    private static string? TryGetFinalPath(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                // Non-Windows resolution is handled by the managed link walk.
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
