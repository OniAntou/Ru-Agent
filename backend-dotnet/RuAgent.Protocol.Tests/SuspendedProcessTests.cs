using System.Diagnostics;
using System.Text;
using Xunit;

namespace RuAgent.Protocol.Tests;

public sealed class SuspendedProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-suspended-" + Guid.NewGuid().ToString("N"));

    public SuspendedProcessTests() => Directory.CreateDirectory(_root);

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
    }
}
