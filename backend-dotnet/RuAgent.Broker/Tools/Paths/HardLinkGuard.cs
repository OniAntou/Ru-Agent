using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RuAgent.Broker.Tools.Paths;

/// <summary>
/// Detects files that are reachable under more than one name on the volume.
/// </summary>
public static class HardLinkGuard
{
    private const uint QueryOnlyDesiredAccess = 0x00000000;
    private const uint FileShareReadWriteDelete = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint BackupSemanticsFlag = 0x02000000;

    /// <summary>
    /// True when the file at <paramref name="path"/> has more than one name on
    /// its volume.
    /// </summary>
    public static bool HasMultipleLinks(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using var handle = CreateFile(
                path,
                QueryOnlyDesiredAccess,
                FileShareReadWriteDelete,
                IntPtr.Zero,
                OpenExisting,
                BackupSemanticsFlag,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return true;
            }

            return !GetFileInformationByHandle(handle, out var information)
                || information.NumberOfLinks > 1;
        }
        catch (Exception)
        {
            return true;
        }
    }

    internal static void RequireSingleLink(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        if (HasMultipleLinks(path))
        {
            throw new UnauthorizedAccessException("hard_link_not_allowed");
        }
    }

    internal static FileStream OpenRead(
        string path,
        FileMode mode,
        FileAccess access,
        FileShare share,
        int bufferSize,
        FileOptions options)
    {
        RequireSingleLink(path);
        var stream = new FileStream(path, mode, access, share, bufferSize, options);
        try
        {
            RequireSingleLink(stream.SafeFileHandle);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static void RequireSingleLink(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (handle.IsInvalid
            || !GetFileInformationByHandle(handle, out var information)
            || information.NumberOfLinks > 1)
        {
            throw new UnauthorizedAccessException("hard_link_not_allowed");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
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
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle hFile,
        out ByHandleFileInformation lpFileInformation);
}
