using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RuAgent.Broker.Tools.Terminal;

/// <summary>
/// Win32 Job Object implementation of <see cref="IProcessTreeController"/>. The job is
/// created with KILL_ON_JOB_CLOSE and without any breakaway allowance, so closing the
/// controller or killing the job terminates the complete process tree.
/// </summary>
public sealed class WindowsJobProcessTree : IProcessTreeController
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectAssignProcessFailed = 5;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private IntPtr _handle = InvalidHandleValue;
    private bool _disposed;

    private WindowsJobProcessTree(IntPtr handle)
    {
        _handle = handle;
    }

    public static WindowsJobProcessTree Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle == IntPtr.Zero || handle == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "job_object_create_failed");
        }

        try
        {
            var limits = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobLimitFlags.KillOnJobClose | JobLimitFlags.DieOnUnhandledException,
            };
            var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = limits,
            };
            if (!SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformation,
                    ref extended,
                    Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "job_object_configure_failed");
            }
        }
        catch
        {
            CloseHandle(handle);
            throw;
        }

        return new WindowsJobProcessTree(handle);
    }

    public JobLimitFlags LimitFlags => JobLimitFlags.KillOnJobClose | JobLimitFlags.DieOnUnhandledException;

    public void Assign(int processId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var processHandle = OpenProcess(0x001F_0FFF /* PROCESS_ALL_ACCESS (legacy) */, false, processId);
        if (processHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "process_open_failed");
        }

        try
        {
            if (!AssignProcessToJobObject(_handle, processHandle))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == JobObjectAssignProcessFailed)
                {
                    throw new Win32Exception(error, "job_breakaway_not_permitted");
                }

                throw new Win32Exception(error, "job_assign_failed");
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    public bool KillTree()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return TerminateJobObject(_handle, unchecked((uint)-1));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle != InvalidHandleValue && _handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
            _handle = InvalidHandleValue;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public JobLimitFlags LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
