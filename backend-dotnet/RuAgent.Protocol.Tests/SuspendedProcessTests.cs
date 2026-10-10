using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class SuspendedProcessTests : IDisposable
{
    private const uint HandleFlagInherit = 0x00000001;
    private static readonly TimeSpan StartupBudget = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan EofBudget = TimeSpan.FromSeconds(5);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-suspended-" + Guid.NewGuid().ToString("N"));

    public SuspendedProcessTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Handle_list_owns_its_values_until_the_attribute_list_is_disposed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (read, write) = CreateInheritablePipe();
        using (read)
        using (write)
        {
            var factory = typeof(SuspendedProcess).Assembly.GetType("RuAgent.Protocol.ProcessAttributeList")!
                .GetMethod("Create", BindingFlags.Static | BindingFlags.Public)!;
            var attribute = factory.Invoke(null, [new[] { write }])!;
            try
            {
                var owner = Assert.IsAssignableFrom<IDisposable>(attribute);
                var values = attribute.GetType().GetProperty("Values")!;
                var pointer = attribute.GetType().GetProperty("Pointer")!;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Assert.Equal(write.DangerousGetHandle(), Marshal.ReadIntPtr((IntPtr)values.GetValue(attribute)!));
                Assert.NotEqual(IntPtr.Zero, (IntPtr)pointer.GetValue(attribute)!);
                owner.Dispose();
                Assert.Equal(IntPtr.Zero, (IntPtr)pointer.GetValue(attribute)!);
            }
            finally
            {
                if (attribute is IDisposable owner) owner.Dispose();
            }
        }
    }

    private static IntPtr ReadPointer(object attribute, string property) =>
        (IntPtr)attribute.GetType().GetProperty(property)!.GetValue(attribute)!;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Child_created_suspended_does_not_execute_before_resume()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var marker = Path.Combine(_root, "marker.txt");
        var script = $"[IO.File]::WriteAllText('{marker.Replace("'", "''")}', 'ran')";
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var started = SuspendedProcess.StartSuspended(startInfo);
        try
        {
            await Task.Delay(1_000);
            Assert.False(File.Exists(marker), "the suspended child executed before Resume");

            Assert.True(SuspendedProcess.Resume(started.ThreadHandle));
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!File.Exists(marker) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(File.Exists(marker), "the resumed child never executed");
        }
        finally
        {
            KillAndDispose(started);
        }
    }

    [Fact]
    public async Task Child_created_suspended_does_not_inherit_unrelated_inheritable_handles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // While another start runs its CreateProcess call, its pipe ends are
        // inheritable in this process. An unrelated inheritable handle stands in
        // for that window: a child that receives it can hold a sibling's pipe
        // open and delay EOF past the sibling's exit.
        var (read, write) = CreateInheritablePipe();
        var started = StartSuspendedPowerShell("Start-Sleep -Seconds 60");
        try
        {
            write.Dispose();
            using var readStream = new FileStream(read, FileAccess.Read);
            var eofTask = Task.Run(readStream.ReadByte);
            var eofBeforeKill = await Task.WhenAny(eofTask, Task.Delay(EofBudget)) == eofTask;
            if (!eofBeforeKill)
            {
                started.Process.Kill();
            }

            Assert.True(eofBeforeKill, "the child inherited an unrelated inheritable handle and withheld EOF");
            Assert.Equal(-1, await eofTask);
        }
        finally
        {
            write.Dispose();
            read.Dispose();
            KillAndDispose(started);
        }
    }

    [Fact]
    public async Task Concurrent_starts_keep_their_stdio_pipes_separate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var starts = await Task.WhenAll(
            Task.Run(() => StartResumedPowerShell("alpha")),
            Task.Run(() => StartResumedPowerShell("bravo")));
        var first = starts[0];
        var second = starts[1];
        try
        {
            Assert.Equal("alpha", await ReadLineWithinAsync(first.StandardOutput, StartupBudget));
            Assert.Equal("bravo", await ReadLineWithinAsync(second.StandardOutput, StartupBudget));

            first.Process.Kill();
            var eofTask = first.StandardOutput.ReadToEndAsync();
            var eofArrived = await Task.WhenAny(eofTask, Task.Delay(EofBudget)) == eofTask;
            Assert.True(eofArrived, "the sibling start kept the killed child's stdout pipe open");
        }
        finally
        {
            KillAndDispose(first);
            KillAndDispose(second);
        }
    }

    private static SuspendedProcess.StartedProcess StartSuspendedPowerShell(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                @"WindowsPowerShell\v1.0\powershell.exe"),
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        return SuspendedProcess.StartSuspended(startInfo);
    }

    private static SuspendedProcess.StartedProcess StartResumedPowerShell(string token)
    {
        var started = StartSuspendedPowerShell($"Write-Output '{token}'; Start-Sleep -Seconds 60");
        if (!SuspendedProcess.Resume(started.ThreadHandle))
        {
            KillAndDispose(started);
            throw new InvalidOperationException("the suspended child could not be resumed");
        }

        return started;
    }

    private static async Task<string?> ReadLineWithinAsync(StreamReader reader, TimeSpan budget)
    {
        var lineTask = reader.ReadLineAsync();
        return await Task.WhenAny(lineTask, Task.Delay(budget)) == lineTask ? await lineTask : null;
    }

    private static void KillAndDispose(SuspendedProcess.StartedProcess started)
    {
        try
        {
            if (!started.Process.HasExited)
            {
                started.Process.Kill();
            }
        }
        catch (Exception)
        {
        }

        started.ThreadHandle.Dispose();
        started.StandardOutput.Dispose();
        started.StandardError.Dispose();
        started.Process.Dispose();
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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create a test pipe.");
        }

        if (!SetHandleInformation(read, HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastWin32Error();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error, "Could not restrict test pipe inheritance.");
        }

        return (read, write);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

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
}
