using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace RuAgent.Updates;

public static class UpdateLauncher
{
    [SupportedOSPlatform("windows")]
    public static async Task StartAsync(DownloadedUpdate update, string installDirectory, string updateRoot, bool restartDesktop, CancellationToken ct)
    {
        var installed = InstalledRelease.Load(installDirectory) ?? throw new UpdateException("update_installation_unknown");
        var expectedAsset = $"Ru-{(installed.ProductKind == UpdateProduct.Desktop ? "Desktop" : "CLI")}-{update.Release.Version}-win-x64-Setup.exe";
        if (update.Release.AssetName != expectedAsset) throw new UpdateException("update_installation_invalid");
        if (restartDesktop && installed.ProductKind != UpdateProduct.Desktop) throw new UpdateException("update_installation_invalid");
        if (ReleaseVersion.Parse(update.Release.Version)?.CompareTo(ReleaseVersion.Parse(installed.Version)) is not > 0)
            throw new UpdateException("update_not_newer");
        var installer = Path.GetFullPath(update.InstallerPath);
        var directory = Path.GetDirectoryName(installer)!;
        if (!string.Equals(Path.GetDirectoryName(directory), Path.GetFullPath(updateRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("update_staging_invalid");
        var helper = Path.Combine(installDirectory, "apply-update.ps1");
        if (!File.Exists(helper) || !File.Exists(installer)) throw new UpdateException("update_helper_missing");
        await using (var stream = File.OpenRead(installer))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false)).Equals(update.Release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("update_digest_mismatch");
        var stagedHelper = Path.Combine(directory, "apply-update.ps1");
        File.Copy(helper, stagedHelper, overwrite: true);
        using var parent = Process.GetCurrentProcess();
        var readyPath = Path.Combine(directory, "worker-ready-" + Guid.NewGuid().ToString("N") + ".txt");
        var request = new
        {
            Protocol = 1, installed.Product, update.Release.Version, update.Release.Sha256,
            InstallerPath = installer, InstallDirectory = Path.GetFullPath(installDirectory),
            ParentPid = parent.Id, ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks,
            RestartDesktop = restartDesktop, ResultPath = Path.Combine(updateRoot, "last-update.json"),
            ReadyPath = readyPath,
        };
        var requestPath = Path.Combine(directory, "update-request.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), ct).ConfigureAwait(false);
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", stagedHelper, "-RequestPath", requestPath })
            start.ArgumentList.Add(argument);
        try
        {
            using var worker = Process.Start(start) ?? throw new UpdateException("update_helper_start_failed");
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                ct.ThrowIfCancellationRequested();
                if (worker.HasExited) throw new UpdateException("update_helper_start_failed");
                if (File.Exists(readyPath))
                {
                    try { if ((await File.ReadAllTextAsync(readyPath, ct).ConfigureAwait(false)).Trim() == "ready") return; }
                    catch (IOException) { /* The worker may still be flushing its acknowledgement. */ }
                }
                await Task.Delay(50, ct).ConfigureAwait(false);
            }
            throw new UpdateException("update_helper_start_timeout");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            File.Delete(requestPath);
            throw new UpdateException("update_helper_start_failed");
        }
        catch
        {
            // A failed/cancelled handoff revokes the staged request. The worker
            // checks it again after the caller exits and before running Setup.
            File.Delete(requestPath);
            throw;
        }
    }
}
