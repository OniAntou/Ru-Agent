using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RuAgent.Broker.Tools.Terminal.ConPty;

public sealed class ConPtySession : IAsyncDisposable, IDisposable
{
    private IntPtr _hPC = IntPtr.Zero;
    private SafeFileHandle? _hPipeIn;
    private SafeFileHandle? _hPipeOut;
    private SafeFileHandle? _hPipePtyIn;
    private FileStream? _inStream;
    private FileStream? _outStream;
    private IntPtr _hProcess = IntPtr.Zero;
    private IntPtr _hThread = IntPtr.Zero;
    private int _processId;
    private bool _disposed;

    public static bool IsSupported =>
        OperatingSystem.IsWindows() && Environment.OSVersion.Version.Build >= 17763;

    public int ProcessId => _processId;
    public Stream OutputStream => _outStream ?? Stream.Null;
    public Stream InputStream => _inStream ?? Stream.Null;

    private ConPtySession() { }

    public static ConPtySession Start(
        string commandLine,
        string workingDirectory,
        short cols = 80,
        short rows = 24)
    {
        if (!IsSupported)
        {
            throw new PlatformNotSupportedException("ConPTY requires Windows 10 build 17763 or newer.");
        }

        var session = new ConPtySession();
        try
        {
            session.Initialize(commandLine, workingDirectory, cols, rows);
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    private void Initialize(string commandLine, string workingDirectory, short cols, short rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLine);
        if (!string.IsNullOrEmpty(workingDirectory) && !Directory.Exists(workingDirectory))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {workingDirectory}");
        }

        var sec = new ConPtyNative.SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<ConPtyNative.SECURITY_ATTRIBUTES>(),
            bInheritHandle = 1,
        };

        if (!ConPtyNative.CreatePipe(out var hPipePtyIn, out var hPipeUserIn, ref sec, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create input pipe.");
        }

        if (!ConPtyNative.CreatePipe(out var hPipeUserOut, out var hPipePtyOut, ref sec, 0))
        {
            hPipePtyIn.Dispose();
            hPipeUserIn.Dispose();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create output pipe.");
        }

        var size = new ConPtyNative.COORD(cols, rows);
        var hr = ConPtyNative.CreatePseudoConsole(size, hPipePtyIn, hPipePtyOut, 0, out _hPC);
        if (hr != ConPtyNative.S_OK)
        {
            hPipePtyIn.Dispose();
            hPipePtyOut.Dispose();
            hPipeUserIn.Dispose();
            hPipeUserOut.Dispose();
            throw new Win32Exception(hr, "Failed to create pseudo console.");
        }

        _hPipePtyIn = hPipePtyIn;
        _hPipeIn = hPipeUserIn;
        _hPipeOut = hPipeUserOut;
        _inStream = new FileStream(_hPipeIn, FileAccess.Write, 4096, isAsync: false);
        _outStream = new FileStream(_hPipeOut, FileAccess.Read, 4096, isAsync: false);

        var startupInfoEx = new ConPtyNative.STARTUPINFOEX();
        startupInfoEx.StartupInfo.cb = Marshal.SizeOf<ConPtyNative.STARTUPINFOEX>();
        startupInfoEx.StartupInfo.dwFlags = 0x00000100;
        startupInfoEx.StartupInfo.hStdInput = IntPtr.Zero;
        startupInfoEx.StartupInfo.hStdOutput = hPipePtyOut.DangerousGetHandle();
        startupInfoEx.StartupInfo.hStdError = hPipePtyOut.DangerousGetHandle();

        var lpSize = IntPtr.Zero;
        ConPtyNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        startupInfoEx.lpAttributeList = Marshal.AllocHGlobal(lpSize);

        if (!ConPtyNative.InitializeProcThreadAttributeList(startupInfoEx.lpAttributeList, 1, 0, ref lpSize))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to initialize attribute list.");
        }

        if (!ConPtyNative.UpdateProcThreadAttribute(
                startupInfoEx.lpAttributeList,
                0,
                (IntPtr)ConPtyNative.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                _hPC,
                (IntPtr)IntPtr.Size,
                IntPtr.Zero,
                IntPtr.Zero))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to update pseudo console attribute.");
        }

        var pSec = new ConPtyNative.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<ConPtyNative.SECURITY_ATTRIBUTES>() };
        var tSec = new ConPtyNative.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<ConPtyNative.SECURITY_ATTRIBUTES>() };

        var creationFlags = ConPtyNative.EXTENDED_STARTUPINFO_PRESENT;

        var created = ConPtyNative.CreateProcessW(
                null,
                commandLine,
                ref pSec,
                ref tSec,
                true,
                creationFlags,
                IntPtr.Zero,
                string.IsNullOrEmpty(workingDirectory) ? null : workingDirectory,
                ref startupInfoEx,
                out var processInfo);

        hPipePtyOut.Dispose();

        if (!created)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to spawn ConPTY process.");
        }

        _hProcess = processInfo.hProcess;
        _hThread = processInfo.hThread;
        _processId = processInfo.dwProcessId;

        ConPtyNative.DeleteProcThreadAttributeList(startupInfoEx.lpAttributeList);
        Marshal.FreeHGlobal(startupInfoEx.lpAttributeList);
    }

    public void Write(string text)
    {
        if (_disposed || _inStream is null) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        _inStream.Write(bytes, 0, bytes.Length);
        _inStream.Flush();
    }

    public void Resize(short cols, short rows)
    {
        if (_hPC != IntPtr.Zero)
        {
            ConPtyNative.ResizePseudoConsole(_hPC, new ConPtyNative.COORD(cols, rows));
        }
    }

    public int AvailableBytes
    {
        get
        {
            if (_hPipeOut is null || _hPipeOut.IsInvalid || _hPipeOut.IsClosed) return 0;
            return ConPtyNative.PeekNamedPipe(_hPipeOut, null, 0, out _, out var totalAvail, out _)
                ? totalAvail
                : 0;
        }
    }

    public async Task<string> ReadOutputUntilExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        var sb = new StringBuilder();
        var buffer = new byte[4096];

        while (!cts.Token.IsCancellationRequested)
        {
            var avail = AvailableBytes;
            if (avail > 0)
            {
                var toRead = Math.Min(buffer.Length, avail);
                var read = _outStream!.Read(buffer, 0, toRead);
                if (read > 0)
                {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
            }
            else
            {
                if (ConPtyNative.WaitForSingleObject(_hProcess, 0) == 0)
                {
                    await Task.Delay(50, cts.Token).ConfigureAwait(false);
                    if (AvailableBytes == 0) break;
                }
                await Task.Delay(30, cts.Token).ConfigureAwait(false);
            }
        }

        return sb.ToString();
    }

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        if (_hProcess == IntPtr.Zero) return -1;
        while (!cancellationToken.IsCancellationRequested)
        {
            var res = ConPtyNative.WaitForSingleObject(_hProcess, 100);
            if (res == 0)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                if (_hPC != IntPtr.Zero)
                {
                    ConPtyNative.ClosePseudoConsole(_hPC);
                    _hPC = IntPtr.Zero;
                }

                if (ConPtyNative.GetExitCodeProcess(_hProcess, out var exitCode))
                {
                    return (int)exitCode;
                }
                return 0;
            }
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        return -1;
    }

    public void Terminate()
    {
        if (_hProcess != IntPtr.Zero)
        {
            ConPtyNative.TerminateProcess(_hProcess, 1);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _inStream?.Dispose();
        _outStream?.Dispose();
        _hPipeIn?.Dispose();
        _hPipeOut?.Dispose();
        _hPipePtyIn?.Dispose();

        if (_hPC != IntPtr.Zero)
        {
            ConPtyNative.ClosePseudoConsole(_hPC);
            _hPC = IntPtr.Zero;
        }

        if (_hThread != IntPtr.Zero)
        {
            ConPtyNative.CloseHandle(_hThread);
            _hThread = IntPtr.Zero;
        }

        if (_processId > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(_processId);
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
            }
            catch { }
        }

        if (_hProcess != IntPtr.Zero)
        {
            try
            {
                if (ConPtyNative.WaitForSingleObject(_hProcess, 0) != 0)
                {
                    ConPtyNative.TerminateProcess(_hProcess, 1);
                }
            }
            catch { }
            ConPtyNative.CloseHandle(_hProcess);
            _hProcess = IntPtr.Zero;
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
