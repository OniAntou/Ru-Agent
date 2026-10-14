using System.Text.Json;
using System.Diagnostics;
using RuAgent.Updates;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class UninstallTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ru-uninstall-test-" + Guid.NewGuid().ToString("N"));
    public UninstallTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void Source_tree_is_never_an_uninstall_target()
    {
        var error = Assert.Throws<UpdateException>(() => UninstallInstallation.Resolve(_root, _root, "anything"));
        Assert.StartsWith("uninstall_installation_unknown", error.Code);
    }

    [Theory]
    [InlineData("desktop")]
    [InlineData("cli")]
    public void Missing_registration_does_not_delete_portable_or_desktop(string product)
    {
        File.WriteAllText(Path.Combine(_root, InstalledRelease.MarkerName),
            JsonSerializer.Serialize(new InstalledRelease(product, "1.0.0", "win-x64")));
        Assert.Throws<UpdateException>(() => UninstallInstallation.Resolve(_root, null, null));
        Assert.True(File.Exists(Path.Combine(_root, InstalledRelease.MarkerName)));
    }

    [Fact]
    public void Registered_uninstaller_is_bound_to_the_exact_cli_directory()
    {
        File.WriteAllText(Path.Combine(_root, InstalledRelease.MarkerName),
            JsonSerializer.Serialize(new InstalledRelease("cli", "1.0.0", "win-x64")));
        var exe = Path.Combine(_root, "unins000.exe");
        File.WriteAllBytes(exe, [1]); File.WriteAllBytes(Path.ChangeExtension(exe, ".dat"), [1]);
        Assert.Equal(exe, UninstallInstallation.Resolve(_root, _root + Path.DirectorySeparatorChar, '"' + exe + '"'));
        Assert.Throws<UpdateException>(() => UninstallInstallation.Resolve(_root, Path.GetTempPath(), exe));
        Assert.Throws<UpdateException>(() => UninstallInstallation.Resolve(_root, _root, exe + " /SILENT"));
        Assert.Throws<UpdateException>(() => UninstallInstallation.Resolve(_root, _root, Path.Combine(Path.GetTempPath(), "unins000.exe")));
    }

    [Fact]
    public async Task Help_and_invalid_options_never_launch_maintenance()
    {
        using var text = new StringWriter();
        Assert.Equal(0, await UninstallCommands.RunAsync(["--help"], text, text, default));
        Assert.Equal(1, await UninstallCommands.RunAsync(["--unknown"], text, text, default));
        Assert.Contains("Sessions and API keys are kept", text.ToString());
    }

    [Fact]
    public async Task Revoked_handoff_exits_without_opening_any_uninstaller()
    {
        var staging = Path.Combine(_root, "worker");
        Directory.CreateDirectory(staging);
        var script = Path.Combine(staging, "uninstall.ps1");
        using (var resource = typeof(UninstallCommands).Assembly.GetManifestResourceStream("RuAgent.Cli.UninstallWorker.ps1")!)
        using (var file = File.Create(script)) resource.CopyTo(file);
        using var parent = Process.GetCurrentProcess();
        var requestPath = Path.Combine(staging, "request.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(new
        {
            Executable = Path.Combine(_root, "unins000.exe"), ParentPid = parent.Id,
            ParentStartTicks = parent.StartTime.ToUniversalTime().Ticks,
        }));
        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            WorkingDirectory = staging, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "-RequestPath", requestPath })
            start.ArgumentList.Add(argument);
        using var worker = Process.Start(start)!;
        try
        {
            var timer = Stopwatch.StartNew();
            while (!File.Exists(Path.Combine(staging, "ready.txt")) && !worker.HasExited && timer.Elapsed < TimeSpan.FromSeconds(10))
                await Task.Delay(50);
            Assert.True(File.Exists(Path.Combine(staging, "ready.txt")));
            File.Delete(requestPath);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, worker.ExitCode);
            Assert.Contains("uninstall_cancelled", File.ReadAllText(Path.Combine(_root, "last-uninstall.json")));
            Assert.False(File.Exists(Path.Combine(_root, "unins000.exe")));
        }
        finally
        {
            File.Delete(requestPath);
            await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
