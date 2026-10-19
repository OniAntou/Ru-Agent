using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RuAgent.Protocol;

public sealed class DirectoryHandleLease : IDisposable
{
    private const uint ShareReadWrite = 3;
    private const uint FileListDirectory = 1;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const int PathBufferSize = 32_768;
    private readonly List<SafeFileHandle> _handles = [];

    public string Identity { get; private set; } = string.Empty;

    public static DirectoryHandleLease Open(string directory, bool requireCanonicalPath = false, bool createMissing = false)
    {
        var fullPath = Path.GetFullPath(directory);
        var lease = new DirectoryHandleLease();
        try
        {
            var current = Path.GetPathRoot(fullPath)!;
            lease.OpenDirectory(current, requireCanonicalPath, createMissing);
            foreach (var component in fullPath[current.Length..].Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                lease.OpenDirectory(current, requireCanonicalPath, createMissing);
            }
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        for (var index = _handles.Count - 1; index >= 0; index--) _handles[index].Dispose();
        _handles.Clear();
    }

    private void OpenDirectory(string path, bool requireCanonicalPath, bool createMissing)
    {
        if (createMissing && !Directory.Exists(path)) Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
            Identity = Path.GetFullPath(path);
            return;
        }
        // Metadata-only (zero-access) handles do not participate in delete
        // sharing checks. Read access makes omission of FILE_SHARE_DELETE hold
        // this directory's name stable until the lease is released.
        var handle = CreateFile(path, FileListDirectory, ShareReadWrite, IntPtr.Zero, OpenExisting, BackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetHRForLastWin32Error();
            handle.Dispose();
            throw new IOException("directory_identity_unverified", error);
        }
        _handles.Add(handle);
        if (!GetFileInformationByHandle(handle, out var info))
            throw new IOException("directory_identity_unverified", Marshal.GetHRForLastWin32Error());
        if (requireCanonicalPath) RequirePath(handle, path);
        Identity = $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
    }

    private static void RequirePath(SafeFileHandle handle, string expected)
    {
        var buffer = new StringBuilder(PathBufferSize);
        var length = GetFinalPathNameByHandle(handle, buffer, PathBufferSize, 0);
        if (length == 0 || length >= PathBufferSize)
            throw new UnauthorizedAccessException("directory_identity_unverified");
        if (!string.Equals(Normalize(buffer.ToString()), Normalize(expected), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("workspace_changed");
    }

    private static string Normalize(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) path = path[4..];
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int length, uint flags);
}
