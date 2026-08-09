using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;

namespace RuApp.Services;

public interface IBackendProcessHandle : IDisposable
{
    IntPtr Handle { get; }
    bool HasExited { get; }
    void Kill();
    bool WaitForExit(TimeSpan timeout);
}

public interface IBackendProcessFactory
{
    IBackendProcessHandle Start(BackendProcessSpec spec);
}

public interface IBackendJob : IDisposable
{
    void Assign(IBackendProcessHandle process);
}

public interface IBackendJobFactory
{
    IBackendJob Create();
}

public interface IBackendReadinessProbe
{
    Task<bool> WaitUntilReadyAsync(
        int publicPort,
        string token,
        CancellationToken cancellationToken);
}

public sealed class WindowsBackendProcessFactory : IBackendProcessFactory
{
    public IBackendProcessHandle Start(BackendProcessSpec spec)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(spec.StandardOutputLogPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(spec.StandardErrorLogPath)!);
        var startInfo = new ProcessStartInfo
        {
            FileName = spec.FileName,
            Arguments = spec.Arguments,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var variable in spec.Environment)
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start {spec.Role}.");
            }

            _ = DrainChildOutputAsync(process, spec);
            return new WindowsBackendProcessHandle(process);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static async Task DrainChildOutputAsync(
        Process process,
        BackendProcessSpec spec)
    {
        try
        {
            await Task.WhenAll(
                CopyOutputAsync(process.StandardOutput, spec.StandardOutputLogPath),
                CopyOutputAsync(process.StandardError, spec.StandardErrorLogPath));
        }
        catch (Exception error)
        {
            Debug.WriteLine($"[BackendProcessManager] Could not persist {spec.Role} output: {error.GetType().Name}");
        }
    }

    private static async Task CopyOutputAsync(StreamReader reader, string path)
    {
        using var writer = new StreamWriter(path, append: true, Encoding.UTF8);
        await writer.WriteLineAsync($"--- {DateTimeOffset.Now:O} ---");
        while (await reader.ReadLineAsync() is { } line)
        {
            await writer.WriteLineAsync(line);
        }
    }
}

public sealed class WindowsBackendProcessHandle : IBackendProcessHandle
{
    private readonly Process _process;

    public WindowsBackendProcessHandle(Process process)
    {
        _process = process;
    }

    public IntPtr Handle => _process.Handle;
    public bool HasExited => _process.HasExited;

    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public bool WaitForExit(TimeSpan timeout)
    {
        var milliseconds = timeout == Timeout.InfiniteTimeSpan
            ? Timeout.Infinite
            : (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        return _process.WaitForExit(milliseconds);
    }

    public void Dispose() => _process.Dispose();
}

public sealed class WindowsBackendJobFactory : IBackendJobFactory
{
    public IBackendJob Create() => new WindowsBackendJob();
}

public sealed class WindowsBackendJob : IBackendJob
{
    private IntPtr _jobHandle;

    public WindowsBackendJob()
    {
        _jobHandle = CreateJobObject(IntPtr.Zero, null);
        if (_jobHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the backend Job Object.");
        }

        try
        {
            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            };
            var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = info,
            };
            var length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var pointer = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extendedInfo, pointer, fDeleteOld: false);
                if (!SetInformationJobObject(
                        _jobHandle,
                        JobObjectInfoType.ExtendedLimitInformation,
                        pointer,
                        (uint)length))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "SetInformationJobObject failed.");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pointer);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Assign(IBackendProcessHandle process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (_jobHandle == IntPtr.Zero || !AssignProcessToJobObject(_jobHandle, process.Handle))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "AssignProcessToJobObject failed.");
        }
    }

    public void Dispose()
    {
        if (_jobHandle == IntPtr.Zero)
        {
            return;
        }

        CloseHandle(_jobHandle);
        _jobHandle = IntPtr.Zero;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9,
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        JobObjectInfoType jobObjectInfoClass,
        IntPtr lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}

public sealed class HttpBackendReadinessProbe : IBackendReadinessProbe
{
    private readonly HttpClient _httpClient;

    public HttpBackendReadinessProbe(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<bool> WaitUntilReadyAsync(
        int publicPort,
        string token,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"http://127.0.0.1:{publicPort}/settings");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (HttpRequestException)
            {
                // The child may still be binding its loopback socket.
            }

            await Task.Delay(500, cancellationToken);
        }

        return false;
    }
}
