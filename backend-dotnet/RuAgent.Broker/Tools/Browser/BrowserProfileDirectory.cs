using System.Diagnostics;
using System.Globalization;

namespace RuAgent.Broker.Tools.Browser;

/// <summary>
/// Creates and removes the ephemeral browser profiles under
/// <c>%LOCALAPPDATA%\Ru\browser</c> (ADR 0055). Each profile name starts with the owning
/// process id, so a launch can delete profiles left by a crashed or force-killed Host without
/// touching a live one.
/// </summary>
internal static class BrowserProfileDirectory
{
    private const int DeleteAttempts = 40;
    private static readonly TimeSpan DeleteDelay = TimeSpan.FromMilliseconds(250);

    internal static string Root() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ru",
        "browser");

    /// <summary>Creates a fresh ephemeral profile and sweeps profiles whose owner is gone.</summary>
    internal static string Create()
    {
        var root = Root();
        Directory.CreateDirectory(root);
        CleanStale(root);
        var profile = Path.Combine(
            root,
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        return profile;
    }

    /// <summary>Deletes a profile after its browser exit, retrying while the browser releases files.</summary>
    internal static async Task DeleteAsync(string directory)
    {
        for (var attempt = 0; attempt < DeleteAttempts; attempt++)
        {
            try
            {
                if (!Directory.Exists(directory)) return;
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                await Task.Delay(DeleteDelay).ConfigureAwait(false);
            }
        }
    }

    private static void CleanStale(string root)
    {
        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(root);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);
            var separator = name.IndexOf('-', StringComparison.Ordinal);
            if (separator > 0
                && int.TryParse(name[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var ownerPid)
                && IsAlive(ownerPid))
            {
                continue;
            }

            _ = DeleteAsync(directory);
        }
    }

    private static bool IsAlive(int processId)
    {
        if (processId == Environment.ProcessId) return true;
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
