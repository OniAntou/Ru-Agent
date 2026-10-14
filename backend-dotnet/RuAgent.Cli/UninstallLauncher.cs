using System.Diagnostics;
using System.Text.Json;
using RuAgent.Updates;

namespace RuAgent.Cli;

internal static class UninstallLauncher
{
    public static async Task<string> StartAsync(string executable, string maintenanceRoot, CancellationToken ct)
    {
        var staging = Path.Combine(maintenanceRoot, "uninstall-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var requestPath = Path.Combine(staging, "request.json");
        var readyPath = Path.Combine(staging, "ready.txt");
        var resultPath = Path.Combine(maintenanceRoot, "last-uninstall.json");
        var script = Path.Combine(staging, "uninstall.ps1");
        await using (var resource = typeof(UninstallLauncher).Assembly.GetManifestResourceStream("RuAgent.Cli.UninstallWorker.ps1")
            ?? throw new UpdateException("uninstall_helper_missing"))
        await using (var file = File.Create(script))
            await resource.CopyToAsync(file, ct).ConfigureAwait(false);
        using var parent = Process.GetCurrentProcess();
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(new
        {
            Executable = executable, ParentPid = parent.Id,
            ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks,
        }), ct).ConfigureAwait(false);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = staging, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-RequestPath", requestPath })
            start.ArgumentList.Add(argument);
        try
        {
            using var worker = Process.Start(start) ?? throw new UpdateException("uninstall_helper_start_failed");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                ct.ThrowIfCancellationRequested();
                if (worker.HasExited) throw new UpdateException("uninstall_helper_start_failed");
                if (File.Exists(readyPath)) return resultPath;
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            throw new UpdateException("uninstall_helper_start_timeout");
        }
        catch
        {
            // Revoking the request prevents a cancelled handoff from uninstalling later.
            File.Delete(requestPath);
            throw;
        }
    }
}
