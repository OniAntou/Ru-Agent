using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RuApp.Services
{
    public class BackendProcessManager : IDisposable
    {
        private Process? _process;
        private IntPtr _jobHandle = IntPtr.Zero;
        public string AuthSecret { get; private set; } = string.Empty;
        public int Port { get; private set; } = 8000;
        public bool IsRunning => _process != null && !_process.HasExited;

        public BackendProcessManager()
        {
            AuthSecret = GenerateSecret();
        }

        private static string GenerateSecret()
        {
            byte[] bytes = new byte[32];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(bytes);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }

        public async Task<bool> StartBackendAsync(string projectRoot, CancellationToken cancellationToken = default)
        {
            string venvPython = Path.Combine(projectRoot, ".venv", "Scripts", "python.exe");
            if (!File.Exists(venvPython))
            {
                venvPython = "python.exe";
            }

            CreateJobObjectWithKillOnClose();

            var startInfo = new ProcessStartInfo
            {
                FileName = venvPython,
                Arguments = $"-m mini_gpt.server --host 127.0.0.1 --port {Port} --secret-token \"{AuthSecret}\"",
                WorkingDirectory = projectRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            _process = new Process { StartInfo = startInfo };
            _process.Start();
            // Drain the redirected stdout/stderr asynchronously. Without a
            // reader the OS pipe (a few KB) fills up once the backend logs
            // enough lines, and the child blocks on write forever.
            _ = DrainChildOutputAsync(_process);

            if (_jobHandle != IntPtr.Zero)
            {
                try
                {
                    AssignProcessToJobObject(_jobHandle, _process.Handle);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BackendProcessManager] AssignProcessToJobObject failed: {ex.Message}");
                }
            }

            using var httpClient = new System.Net.Http.HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AuthSecret);

            for (int i = 0; i < 30; i++)
            {
                if (cancellationToken.IsCancellationRequested) return false;
                try
                {
                    var response = await httpClient.GetAsync($"http://127.0.0.1:{Port}/settings", cancellationToken);
                    if (response.IsSuccessStatusCode)
                    {
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BackendProcessManager] Backend not ready yet ({i + 1}/30): {ex.Message}");
                }
                await Task.Delay(500, cancellationToken);
            }

            return IsRunning;
        }

        private void CreateJobObjectWithKillOnClose()
        {
            try
            {
                _jobHandle = CreateJobObject(IntPtr.Zero, null);
                if (_jobHandle == IntPtr.Zero) return;

                var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                };

                var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
                {
                    BasicLimitInformation = info
                };

                int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
                IntPtr extendedInfoPtr = Marshal.AllocHGlobal(length);
                try
                {
                    Marshal.StructureToPtr(extendedInfo, extendedInfoPtr, false);
                    SetInformationJobObject(_jobHandle, JobObjectInfoType.ExtendedLimitInformation, extendedInfoPtr, (uint)length);
                }
                finally
                {
                    Marshal.FreeHGlobal(extendedInfoPtr);
                }
            }
            catch
            {
                _jobHandle = IntPtr.Zero;
            }
        }

        private static async Task DrainChildOutputAsync(Process process)
        {
            try
            {
                await Task.WhenAll(
                    process.StandardOutput.ReadToEndAsync(),
                    process.StandardError.ReadToEndAsync()
                );
            }
            catch
            {
                // Streams end when the child exits; nothing else to do.
            }
        }

        public void Dispose()
        {
            var process = Interlocked.Exchange(ref _process, null);
            if (process == null || process.HasExited)
            {
                return;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[BackendProcessManager] Kill failed: {ex.Message}");
            }
            finally
            {
                try
                {
                    process.WaitForExit(500);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BackendProcessManager] WaitForExit failed: {ex.Message}");
                }
                process.Dispose();
            }

            if (_jobHandle != IntPtr.Zero)
            {
                try
                {
                    CloseHandle(_jobHandle);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[BackendProcessManager] CloseHandle failed: {ex.Message}");
                }
                _jobHandle = IntPtr.Zero;
            }
        }

        #region Win32 P/Invoke
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        private enum JobObjectInfoType
        {
            ExtendedLimitInformation = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public Int64 PerProcessUserTimeLimit;
            public Int64 PerJobUserTimeLimit;
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
            public UInt64 ReadOperationCount;
            public UInt64 WriteOperationCount;
            public UInt64 OtherOperationCount;
            public UInt64 ReadTransferCount;
            public UInt64 WriteTransferCount;
            public UInt64 OtherTransferCount;
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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(IntPtr hJob, JobObjectInfoType JobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);
        #endregion
    }
}
