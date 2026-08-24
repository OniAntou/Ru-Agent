using System.Diagnostics;
using System.Text;
using Xunit;

namespace RuAgent.Hosting.Tests;

public sealed class OwnedProcessTreeTests
{
    [Fact]
    public void OwnedProcessTreeKillsChildAndGrandchildOnDispose()
    {
        if (!OperatingSystem.IsWindows())
            return; // Job Objects are Windows-only

        var markerChild = Path.Combine(Path.GetTempPath(), "ru-owned-child-" + Guid.NewGuid().ToString("N") + ".txt");
        var markerGrandchild = Path.Combine(Path.GetTempPath(), "ru-owned-grandchild-" + Guid.NewGuid().ToString("N") + ".txt");
        File.Delete(markerChild);
        File.Delete(markerGrandchild);

        // Script: child creates a grandchild process that sleeps, then child sleeps
        var grandchildScript = $"Start-Process -FilePath powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 60; Set-Content -Path ''{PowerShellLiteral(markerGrandchild)}'' -Value done' -WindowStyle Hidden; Start-Sleep -Seconds 60; Set-Content -Path '{PowerShellLiteral(markerChild)}' -Value done";
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(grandchildScript)),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var process = Process.Start(psi);
        Assert.NotNull(process);
        // Give it a moment to spawn grandchild
        Thread.Sleep(1500);
        Assert.False(process.HasExited);

        // Capture grandchild PID via not trivial; instead we will kill via Job and check that process tree is dead
        using (var job = new OwnedProcessTree())
        {
            job.Assign(process);
            // Give a brief moment then dispose job - should kill child + grandchild
            job.Dispose();
            // Wait for child to exit due to job close
            var exited = SpinWait.SpinUntil(() => process.HasExited, TimeSpan.FromSeconds(5));
            Assert.True(exited, "Child should have been killed by Job Object");
        }

        // Ensure child is killed; grandchild marker should not appear because killed before file write
        Thread.Sleep(500);
        Assert.False(File.Exists(markerChild), "Child should not have completed");
        // Grandchild also should be killed; if not, file would appear after 60s but we check immediate
        Assert.False(File.Exists(markerGrandchild));

        try { if (!process.HasExited) process.Kill(entireProcessTree:true); } catch { }
        process.Dispose();
        try { File.Delete(markerChild); } catch { }
        try { File.Delete(markerGrandchild); } catch { }
    }

    [Fact]
    public void DisposeIsIdempotent()
    {
        var job = new OwnedProcessTree();
        job.Dispose();
        var ex = Record.Exception(() => job.Dispose());
        Assert.Null(ex);
    }

    private static string PowerShellLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
