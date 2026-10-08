using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using RuAgent.Broker.Tools.Paths;

namespace RuAgent.Broker.Tools.Files;

internal static class FileDeletionHandle
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ShareRead = 1;
    private const uint OpenExisting = 3;
    private const int FileDispositionInfo = 4;
    private const int BufferSize = 4096;

    public static FileStream Open(string path)
    {
        HardLinkGuard.RequireSingleLink(path);
        var handle = CreateFile(path, GenericRead | DeleteAccess, ShareRead, IntPtr.Zero,
            OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetHRForLastWin32Error();
            handle.Dispose();
            throw new IOException("Could not open the file for deletion.", error);
        }
        try
        {
            HardLinkGuard.RequireSingleLink(handle);
            return new FileStream(handle, FileAccess.Read, BufferSize, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static void Delete(FileStream stream)
    {
        var info = new DispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(stream.SafeFileHandle, FileDispositionInfo, ref info,
                (uint)Marshal.SizeOf<DispositionInfo>()))
            throw new IOException("Could not delete the validated file.", Marshal.GetHRForLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispositionInfo
    {
        [MarshalAs(UnmanagedType.U1)] public bool DeleteFile;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security,
        uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass,
        ref DispositionInfo information, uint size);
}
