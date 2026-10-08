using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RuApp.Services;

/// <summary>
/// One interactive console for the Terminal tab: a real Windows pseudo console
/// (ConPTY) hosting the user's own shell, with raw UTF-8 bytes in both
/// directions. The child is assigned to a kill-on-close Job Object, so closing
/// the app can never leave a shell or its descendants behind.
///
/// This is the user's terminal, not a model tool: nothing here is visible to or
/// callable by the Agent, and the Broker stays the only execution authority for
/// model-visible tool calls.
/// </summary>
public sealed class ConPtyTerminalSession : IDisposable
{
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int ReadBufferBytes = 4096;

    private IntPtr _pseudoConsole;
    private IntPtr _job;
    private IntPtr _processHandle;
    private FileStream? _inputStream;
    private FileStream? _outputStream;
    private Thread? _readThread;
    private volatile bool _disposed;

    /// <summary>Raw console output bytes (UTF-8 with escape sequences).</summary>
    public event Action<byte[]>? OutputReceived;

    /// <summary>Raised once when the console's output stream ends.</summary>
    public event Action? Exited;

    public int ProcessId { get; private set; }

    public static ConPtyTerminalSession Start(string workingDirectory, string commandLine, short columns, short rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);

        var session = new ConPtyTerminalSession();
        try
        {
            session.StartCore(workingDirectory, commandLine, columns, rows);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public void Write(byte[] data)
    {
        if (_disposed || data.Length == 0 || _inputStream is null)
        {
            return;
        }

        try
        {
            _inputStream.Write(data, 0, data.Length);
            _inputStream.Flush();
        }
        catch (IOException)
        {
            // The console ended between the check and the write; the read loop
            // reports the exit.
        }
    }

    public void Resize(short columns, short rows)
    {
        if (_disposed || _pseudoConsole == IntPtr.Zero)
        {
            return;
        }

        _ = ResizePseudoConsole(_pseudoConsole, new COORD { X = columns, Y = rows });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_pseudoConsole != IntPtr.Zero)
        {
            ClosePseudoConsole(_pseudoConsole);
            _pseudoConsole = IntPtr.Zero;
        }

        try { _inputStream?.Dispose(); } catch (IOException) { }
        try { _outputStream?.Dispose(); } catch (IOException) { }

        if (_processHandle != IntPtr.Zero)
        {
            _ = WaitForSingleObject(_processHandle, 2_000);
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }

        if (_job != IntPtr.Zero)
        {
            _ = TerminateJobObject(_job, 0);
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }

        _readThread?.Join(TimeSpan.FromSeconds(2));
    }

    private void StartCore(string workingDirectory, string commandLine, short columns, short rows)
    {
        var security = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = 0,
        };

        if (!CreatePipe(out var inputRead, out var inputWrite, ref security, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(input) failed");
        }

        if (!CreatePipe(out var outputRead, out var outputWrite, ref security, 0))
        {
            CloseHandle(inputRead);
            CloseHandle(inputWrite);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe(output) failed");
        }

        int createResult = CreatePseudoConsole(new COORD { X = columns, Y = rows }, inputRead, outputWrite, 0, out _pseudoConsole);
        if (createResult != 0)
        {
            CloseHandle(inputRead);
            CloseHandle(inputWrite);
            CloseHandle(outputRead);
            CloseHandle(outputWrite);
            Marshal.ThrowExceptionForHR(createResult);
        }

        // The pseudo console duplicates the PTY-side pipe ends into conhost;
        // our copies are no longer needed (matching the ConPTY sample).
        CloseHandle(inputRead);
        CloseHandle(outputWrite);

        IntPtr attributeList = IntPtr.Zero;
        IntPtr attributeListSize = IntPtr.Zero;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
        attributeList = Marshal.AllocHGlobal(attributeListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
            }

            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)ProcThreadAttributePseudoConsole,
                    _pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
            }

            var startup = new STARTUPINFOEX { lpAttributeList = attributeList };
            startup.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            // A parent whose own standard handles were redirected hands those stale
            // values to its child by default, and the console attach then leaves the
            // child with dead handles; declaring empty standard handles breaks that
            // chain so the pseudo console initializes the child's handles itself.
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
            startup.StartupInfo.hStdInput = IntPtr.Zero;
            startup.StartupInfo.hStdOutput = IntPtr.Zero;
            startup.StartupInfo.hStdError = IntPtr.Zero;
            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startup,
                    out var process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessW failed");
            }

            CloseHandle(process.hThread);
            _processHandle = process.hProcess;
            ProcessId = process.dwProcessId;

            AttachJob(_processHandle);

            _inputStream = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write);
            _outputStream = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read);
        }
        finally
        {
            DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }

        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "RuTerminalRead",
        };
        _readThread.Start();
    }

    private void AttachJob(IntPtr processHandle)
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
        {
            return;
        }

        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int limitsSize = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        IntPtr limitsPointer = Marshal.AllocHGlobal(limitsSize);
        try
        {
            Marshal.StructureToPtr(limits, limitsPointer, false);
            _ = SetInformationJobObject(_job, JobObjectExtendedLimitInformationClass, limitsPointer, (uint)limitsSize);
        }
        finally
        {
            Marshal.FreeHGlobal(limitsPointer);
        }

        _ = AssignProcessToJobObject(_job, processHandle);
    }

    private void ReadLoop()
    {
        var buffer = new byte[ReadBufferBytes];
        try
        {
            while (!_disposed)
            {
                int read = _outputStream!.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                var chunk = new byte[read];
                Buffer.BlockCopy(buffer, 0, chunk, 0, read);
                OutputReceived?.Invoke(chunk);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Closing the console ends the stream; that is the normal exit path.
        }
        finally
        {
            if (!_disposed)
            {
                Exited?.Invoke();
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
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
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);
}
