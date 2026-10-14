using RuAgent.Client;
using RuAgent.Updates;

namespace RuAgent.Cli;

public static class UninstallCommands
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (args.Length > 0)
        {
            await error.WriteLineAsync("Usage: ru uninstall\nOpens the Windows CLI uninstaller for confirmation. Sessions and API keys are kept.");
            return args is ["--help"] or ["-h"] ? 0 : 1;
        }
        try
        {
            var executable = UninstallInstallation.Resolve(AppContext.BaseDirectory);
            await UpdateMaintenance.PrepareAsync(ct).ConfigureAwait(false);
            var resultPath = await UninstallLauncher.StartAsync(executable, new UpdatePreferences().DirectoryPath, ct).ConfigureAwait(false);
            await output.WriteLineAsync("Opening the Windows CLI uninstaller after Ru exits. Confirm there to remove CLI and its PATH entry; sessions and API keys are kept.");
            await output.WriteLineAsync($"Uninstall result: {resultPath}");
            return 0;
        }
        catch (OperationCanceledException) { await error.WriteLineAsync("Uninstall cancelled."); return 130; }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException
            or ArgumentException or System.ComponentModel.Win32Exception or HttpRequestException
            or System.Text.Json.JsonException or System.Security.SecurityException)
        {
            var message = ex is UpdateException { Code: "update_busy" }
                ? "uninstall_busy: finish or cancel unfinished runs before removing CLI"
                : ex.Message;
            await error.WriteLineAsync($"Uninstall failed: {message}");
            return 1;
        }
    }
}
