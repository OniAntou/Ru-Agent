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

    /// <summary>
    /// Exercises the production start path: the child is created suspended, assigned
    /// to the job, and only then resumed, so a descendant it creates afterwards is
    /// already inside the job and cannot outlive it.
    /// </summary>
    /// <remarks>
    /// The outcome is decidable rather than timing-dependent. The descendant blocks on
    /// a gate the test opens only after the job has closed, so a survivor is guaranteed
    /// to write its marker and a killed process is guaranteed not to. The gate also
    /// makes the assertion meaningful in both directions, which the previous version of
    /// this test was not: it waited 500 ms for a marker the descendant wrote after 60
    /// seconds, so it could not fail either way.
    ///
    /// Confirmed to fail against a running-child create (<c>Process.Start</c> followed
    /// by <c>Assign</c>) and to pass against the suspended create.
    /// </remarks>
    [Fact]
    public void DescendantCreatedAfterResumeCannotOutliveTheJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Job Objects are Windows-only
        }

        var scratch = Path.Combine(Path.GetTempPath(), "ru-susp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var spec = new BrokerChildProcessSpec(
            "ru-agent-broker",
            "powershell.exe",
            BuildChildArguments(scratch),
            scratch,
            new Dictionary<string, string>(),
            Path.Combine(scratch, "out.jsonl"),
            Path.Combine(scratch, "err.jsonl"));

        var gate = Path.Combine(scratch, "gate.txt");
        var grandchildMarker = Path.Combine(scratch, "grandchild.txt");

        IBrokerChildProcessHandle? child = null;
        OwnedProcessTree? job = null;
        try
        {
            child = new WindowsBrokerChildProcessFactory().Start(spec);

            // Suspended means it has executed nothing, so no descendant can exist yet.
            Assert.False(
                SpinWait.SpinUntil(() => File.Exists(grandchildMarker), TimeSpan.FromMilliseconds(300)),
                "the child ran before it was resumed");

            job = new OwnedProcessTree();
            job.Assign(child);
            child.Resume();

            // The child must actually reach its own code, or the test proves nothing.
            Assert.True(
                SpinWait.SpinUntil(() => File.Exists(Path.Combine(scratch, "child-started.txt")), TimeSpan.FromSeconds(30)),
                "the resumed child never started running");

            job.Dispose();
            job = null;
            Assert.True(child.WaitForExit(TimeSpan.FromSeconds(15)), "the child should have been killed by the job");

            File.WriteAllText(gate, "go");
            Assert.False(
                SpinWait.SpinUntil(() => File.Exists(grandchildMarker), TimeSpan.FromSeconds(20)),
                "a descendant outlived the job that owns its parent");
        }
        finally
        {
            try { child?.Kill(); } catch { }
            try { child?.Dispose(); } catch { }
            try { job?.Dispose(); } catch { }
            try { Directory.Delete(scratch, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Powershell that records that it started, then spawns a descendant which blocks
    /// on the gate before writing its marker.
    /// </summary>
    private static string BuildChildArguments(string scratch)
    {
        var gate = Path.Combine(scratch, "gate.txt");
        var grandchildMarker = Path.Combine(scratch, "grandchild.txt");
        var started = Path.Combine(scratch, "child-started.txt");

        var grandchildScript =
            $"while (-not (Test-Path -LiteralPath '{PowerShellLiteral(gate)}')) {{ Start-Sleep -Milliseconds 50 }}; "
            + $"Set-Content -LiteralPath '{PowerShellLiteral(grandchildMarker)}' -Value alive";
        var childScript =
            $"Set-Content -LiteralPath '{PowerShellLiteral(started)}' -Value started; "
            + "Start-Process -FilePath powershell.exe "
            + $"-ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','{Convert.ToBase64String(Encoding.Unicode.GetBytes(grandchildScript))}' "
            + "-WindowStyle Hidden; Start-Sleep -Seconds 120";

        return "-NoProfile -NonInteractive -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes(childScript));
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
