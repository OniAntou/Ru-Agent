using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RuAgent.Broker.Tools.Paths;

/// <summary>
/// Detects files that are reachable under more than one name on the volume.
/// </summary>
/// <remarks>
/// The workspace boundary is enforced by canonicalizing a path and testing
/// whether its final target sits inside the root. An NTFS hard link defeats that
/// test completely: <c>mklink /H &lt;workspace&gt;\leak.txt &lt;outside&gt;\secret</c>
/// needs no privilege, and the resulting name is an ordinary in-workspace file.
/// There is no reparse point, so <c>FileSystemInfo.LinkTarget</c> is null and the
/// attributes carry no <c>ReparsePoint</c> flag, and
/// <c>GetFinalPathNameByHandle</c> — which resolves one name for the file object —
/// reports the workspace path. The resolver therefore classifies it as
/// <c>InsideWorkspace</c> and reading it returns the outside file's bytes.
///
/// A link count above one is the only signal available: the handle API reports
/// how many names point at the file, never where the other names are. The guard
/// consequently cannot tell an in-workspace link from one that escapes, and it
/// refuses both rather than guessing. That is the fail-closed choice, and it
/// costs no legitimate capability — the file's own content is still readable
/// through its original name.
///
/// Only the read path consults this. Broker mutations go through
/// <c>AtomicFileReplacement</c>, which writes a temporary file and moves it over
/// the target, swapping the directory entry instead of writing through the link;
/// a hard link there cannot redirect the write outside the workspace.
/// </remarks>
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
    /// <remarks>
    /// An unreadable or absent target reports false: this guard only answers the
    /// link question, and the caller's own open reports the real failure with its
    /// own error code.
    /// </remarks>
    public static bool HasMultipleLinks(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !OperatingSystem.IsWindows())
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
                return false;
            }

            return GetFileInformationByHandle(handle, out var information)
                && information.NumberOfLinks > 1;
        }
        catch (Exception)
        {
            // Best effort: an unreadable target is the caller's error to report.
            return false;
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
