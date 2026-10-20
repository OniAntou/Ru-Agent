using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RuAgent.Updates;
using Xunit;
namespace RuAgent.Updates.Tests;
public sealed class LauncherTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_exits_only_after_helper_acknowledges(bool acknowledge)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(),"ru-handoff-"+Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root,"install"); var updates = Path.Combine(root,"updates"); var staging = Path.Combine(updates,"staging");
        Directory.CreateDirectory(install); Directory.CreateDirectory(staging);
        try
        {
            File.WriteAllText(Path.Combine(install,InstalledRelease.MarkerName),JsonSerializer.Serialize(new InstalledRelease("cli","1.0.0","win-x64")));
            // A harmless owned child writes only to this private fixture. It
            // never runs the staged .exe or touches a live Ru installation.
            File.WriteAllText(Path.Combine(install,"apply-update.ps1"),acknowledge
                ? "param([string]$RequestPath)\n$r=Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json\nSet-Content -LiteralPath ($r.ReadyPath + '.pid') -Value $PID -Encoding ascii\nSet-Content -LiteralPath $r.ReadyPath -Value ready -Encoding ascii\nStart-Sleep -Seconds 1\n"
                : "param([string]$RequestPath)\nexit 1\n");
            var file = Path.Combine(staging,"Ru-CLI-2.0.0-win-x64-Setup.exe"); byte[] bytes = [1,2,3,4]; File.WriteAllBytes(file,bytes);
            var release = new ReleaseUpdate("2.0.0",Path.GetFileName(file),new Uri("https://github.com/OniAntou/Ru-Agent/releases/download/v2.0.0/"+Path.GetFileName(file)),Convert.ToHexString(SHA256.HashData(bytes)),bytes.Length,new Uri("https://github.com/OniAntou/Ru-Agent/releases/tag/v2.0.0"));
            var task = UpdateLauncher.StartAsync(new(release,file),install,updates,false,default);
            if (acknowledge)
            {
                await task; Assert.True(File.Exists(Path.Combine(staging,"update-request.json")));
                var signal = Directory.GetFiles(staging,"worker-ready-*.txt").Single();
                var processId = int.Parse(File.ReadAllText(signal + ".pid").Trim());
                try
                {
                    using var child = Process.GetProcessById(processId);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                catch (ArgumentException) { /* The owned fixture child already exited. */ }
            }
            else { Assert.Equal("update_helper_start_failed",(await Assert.ThrowsAsync<UpdateException>(() => task)).Code); Assert.False(File.Exists(Path.Combine(staging,"update-request.json"))); }
        }
        finally { await CleanupFixtureAsync(root); }
    }

    private static async Task CleanupFixtureAsync(string root)
    {
        var resolved = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(resolved).StartsWith("ru-handoff-", StringComparison.Ordinal))
            throw new InvalidOperationException("Refusing to clean an unowned fixture directory.");

        // The fixture worker has exited, but Windows can retain a sharing lock
        // briefly. Retry only that cleanup condition, within a bounded deadline.
        var deadline = Stopwatch.StartNew();
        while (Directory.Exists(resolved))
        {
            try { Directory.Delete(resolved, recursive: true); return; }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33
                && deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                await Task.Delay(50);
            }
        }
    }
}
