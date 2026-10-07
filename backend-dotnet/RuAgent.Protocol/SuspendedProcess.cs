using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RuAgent.Protocol;

/// <summary>
/// Starts a child process suspended, so it can be placed in a Job Object before it
/// executes a single instruction.
/// </summary>
/// <remarks>
/// <see cref="Process.Start"/> returns after the child's main thread is already
/// running. Assigning the child to a job afterwards leaves a window in which the
/// child can create a descendant that belongs to no job this application closes, so
/// that descendant outlives the run. Measured on Windows: the direct child dies with
/// the job, the descendant does not.
///
/// A child created with <c>CREATE_SUSPENDED</c> cannot run user code until
/// <c>ResumeThread</c>, so no descendant can exist before the assignment. Verified to
/// close the window end to end.
///
/// .NET still performs the process creation, so stdout/stderr capture, the working
/// directory and the environment keep their existing shape: the read ends of two
/// pipes are wrapped in <see cref="StreamReader"/> exactly as
/// <c>Process.StandardOutput</c>/<c>StandardError</c> would be, and the caller's
/// <see cref="ProcessStartInfo.Environment"/> is written to a Unicode environment
/// block. <see cref="ProcessStartInfo.ArgumentList"/> is honored when present and
/// <see cref="ProcessStartInfo.Arguments"/> otherwise. Inheritance is restricted to
/// the child's three standard handles with <c>PROC_THREAD_ATTRIBUTE_HANDLE_LIST</c>,
/// so an unrelated inheritable handle in this process — another start's pipe end, for
/// example — can never cross into the child and keep a sibling's stream open. .NET's
/// own automatic job object is irrelevant here: the child is out of any job once it
/// is assigned to this one, and because it was suspended there is nothing left
/// behind when that happens.
/// </remarks>
public static class SuspendedProcess
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const int ErrorInsufficientBuffer = 122;
    private const int ProcThreadAttributeHandleList = 0x00020002;

    public sealed record StartedProcess(
        Process Process,
        SafeFileHandle ThreadHandle,
        StreamReader StandardOutput,
        StreamReader StandardError);

    /// <summary>
    /// Creates the process described by <paramref name="startInfo"/> without letting it
    /// run. The caller must assign it to a job and then call <see cref="Resume"/>.
    /// </summary>
    public static StartedProcess StartSuspended(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Suspended process creation requires Windows.");
        }

        SafeFileHandle? stdinRead = null;
        SafeFileHandle? stdinWrite = null;
        SafeFileHandle? stdoutRead = null;
        SafeFileHandle? stdoutWrite = null;
        SafeFileHandle? stderrRead = null;
        SafeFileHandle? stderrWrite = null;
        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        var inheritHandles = new List<SafeFileHandle>();
        try
        {
            // stdin is an empty pipe whose write end closes immediately: the child reads
            // EOF instead of inheriting a console. The read end stays inheritable so it
            // crosses into the child as its standard input.
            (stdinRead, stdinWrite) = CreateInheritablePipe();
            stdinWrite.Dispose();
            stdinWrite = null;

            (stdoutRead, stdoutWrite) = CreateInheritablePipe();
            ClearInheritance(stdoutRead);
            (stderrRead, stderrWrite) = CreateInheritablePipe();
            ClearInheritance(stderrRead);

            // Only the child's own standard handles may cross into it.
            inheritHandles.Add(stdinRead);
            inheritHandles.Add(stdoutWrite);
            inheritHandles.Add(stderrWrite);

            var commandLine = BuildCommandLine(startInfo);
            environmentBlock = BuildEnvironmentBlock(startInfo.Environment);
            attributeList = CreateHandleListAttribute(inheritHandles);

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = stdinRead.DangerousGetHandle(),
                    hStdOutput = stdoutWrite.DangerousGetHandle(),
                    hStdError = stderrWrite.DangerousGetHandle(),
                },
                lpAttributeList = attributeList,
            };

            var workingDirectory = string.IsNullOrWhiteSpace(startInfo.WorkingDirectory)
                ? null
                : startInfo.WorkingDirectory;

            var created = CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,
                CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent,
                environmentBlock,
                workingDirectory,
                ref startupInfo,
                out var processInformation);
            if (!created)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not create {startInfo.FileName}.");
            }

            // The process handle is not needed: Process opens its own. The thread handle
            // is what ResumeThread needs.
            CloseHandle(processInformation.hProcess);

            // The child owns its inherited copy of the stdin read end.
            SafeClose(stdinRead);
            stdinRead = null;

            SafeFileHandle? stdout = stdoutRead;
            SafeFileHandle? stderr = stderrRead;
            stdoutRead = null;
            stderrRead = null;

            var threadHandle = new SafeFileHandle(processInformation.hThread, ownsHandle: true);
            Process? process = null;
            StreamReader? standardOutput = null;
            StreamReader? standardError = null;
            try
            {
                // Process.StandardOutput/StandardError are only populated by Process.Start,
                // so the readers are handed back directly rather than attached to it.
                process = Process.GetProcessById(processInformation.dwProcessId);
                standardOutput = new StreamReader(
                    new FileStream(stdout, FileAccess.Read),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                stdout = null;
                standardError = new StreamReader(
                    new FileStream(stderr, FileAccess.Read),
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                stderr = null;

                return new StartedProcess(process, threadHandle, standardOutput, standardError);
            }
            catch
            {
                SafeClose(stdout);
                SafeClose(stderr);
                standardOutput?.Dispose();
                standardError?.Dispose();
                try
                {
                    if (process is not null && !process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception)
                {
                }

                process?.Dispose();
                threadHandle.Dispose();
                throw;
            }
        }
        catch
        {
            SafeClose(stdinRead);
            SafeClose(stdoutRead);
            SafeClose(stderrRead);
            throw;
        }
        finally
        {
            SafeClose(stdinWrite);
            SafeClose(stdoutWrite);
            SafeClose(stderrWrite);
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    /// <summary>
    /// Lets the suspended main thread run. Returns false when the thread is already
    /// gone, which means the process exited while it was suspended.
    /// </summary>
    public static bool Resume(SafeFileHandle threadHandle)
    {
        ArgumentNullException.ThrowIfNull(threadHandle);
        if (threadHandle.IsInvalid || threadHandle.IsClosed)
        {
            return false;
        }

        return ResumeThread(threadHandle) != unchecked((uint)-1);
    }

    private static (SafeFileHandle Read, SafeFileHandle Write) CreateInheritablePipe()
    {
        var attributes = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = 1,
        };
        if (!CreatePipe(out var read, out var write, ref attributes, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a child process pipe.");
        }

        return (read, write);
    }

    private static void ClearInheritance(SafeFileHandle handle)
    {
        if (!SetHandleInformation(handle, HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Could not restrict pipe handle inheritance.");
        }
    }

    private static IntPtr CreateHandleListAttribute(IReadOnlyList<SafeFileHandle> handles)
    {
        var size = IntPtr.Zero;
        if (!InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error, "Could not size the child handle list.");
            }
        }

        var list = Marshal.AllocHGlobal(size);
        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            var error = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(list);
            throw new Win32Exception(error, "Could not create the child handle list.");
        }

        var values = new IntPtr[handles.Count];
        for (var index = 0; index < handles.Count; index++)
        {
            values[index] = handles[index].DangerousGetHandle();
        }

        var pinned = GCHandle.Alloc(values, GCHandleType.Pinned);
        try
        {
            if (!UpdateProcThreadAttribute(
                    list,
                    0,
                    (IntPtr)ProcThreadAttributeHandleList,
                    pinned.AddrOfPinnedObject(),
                    (IntPtr)(values.Length * IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                DeleteProcThreadAttributeList(list);
                Marshal.FreeHGlobal(list);
                throw new Win32Exception(error, "Could not restrict child handle inheritance.");
            }
        }
        finally
        {
            pinned.Free();
        }

        return list;
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var builder = new StringBuilder();
        AppendQuoted(builder, startInfo.FileName);
        if (startInfo.ArgumentList.Count > 0)
        {
            foreach (var argument in startInfo.ArgumentList)
            {
                builder.Append(' ');
                AppendArgument(builder, argument);
            }

            return builder.ToString();
        }

        if (!string.IsNullOrWhiteSpace(startInfo.Arguments))
        {
            builder.Append(' ');
            builder.Append(startInfo.Arguments);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Quotes one argument only when it needs quoting, matching the command line
    /// <c>Process.Start</c> builds from <see cref="ProcessStartInfo.ArgumentList"/>.
    /// Always quoting breaks programs that parse their own command line, such as
    /// <c>cmd.exe /c</c>, which would read <c>"/c"</c> as the command text.
    /// </summary>
    private static void AppendArgument(StringBuilder builder, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            builder.Append(argument);
            return;
        }

        AppendQuoted(builder, argument);
    }

    /// <summary>
    /// Quotes one argument for the Windows command line the way the C runtime parses it.
    /// </summary>
    private static void AppendQuoted(StringBuilder builder, string argument)
    {
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                // Backslashes before a quote must be doubled, and the quote escaped.
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(character);
        }

        // Trailing backslashes would escape the closing quote.
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
    }

    /// <summary>
    /// Writes the caller's environment as a Unicode environment block (sorted,
    /// double-null terminated), which is what <c>CREATE_UNICODE_ENVIRONMENT</c> expects.
    /// </summary>
    private static IntPtr BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        var builder = new StringBuilder();
        foreach (var pair in environment
            .Where(pair => !string.IsNullOrEmpty(pair.Key) && pair.Value is not null)
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
        }

        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    private static void SafeClose(SafeFileHandle? handle)
    {
        try { handle?.Dispose(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfoEx lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        ref SecurityAttributes lpPipeAttributes,
        int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle hObject, uint dwMask, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
