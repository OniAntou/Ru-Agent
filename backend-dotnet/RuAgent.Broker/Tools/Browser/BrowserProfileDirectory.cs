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

/// <summary>
/// The profile one browser process runs with (ADR 0055, amended 2026-11-25): an ephemeral profile
/// that is deleted with the process, or Ru's persistent profile under
/// <c>%LOCALAPPDATA%\Ru\browser-profile\&lt;family&gt;</c>, which keeps cookies and sign-ins between
/// runs. The persistent profile is Ru's own, never the user's browser profile, and a lock file
/// keeps a second Ru Host from starting the same profile at once.
/// </summary>
internal sealed class BrowserProfileLease : IAsyncDisposable
{
    private const string LockFileName = "ru-profile.lock";
    private readonly FileStream? _lock;
    private int _disposed;

    private BrowserProfileLease(string directory, FileStream? lockHandle)
    {
        Directory = directory;
        _lock = lockHandle;
    }

    public string Directory { get; }

    public bool IsPersistent => _lock is not null;

    /// <summary>Tests point the persistent profile at a temporary folder; the product never sets it.</summary>
    internal static string? PersistentRootOverride { get; set; }

    internal static string PersistentRoot() => PersistentRootOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ru",
        "browser-profile");

    public static BrowserProfileLease Ephemeral() => new(BrowserProfileDirectory.Create(), null);

    /// <summary>Opens Ru's persistent profile for one browser family; fails by name while another Host holds it.</summary>
    public static BrowserProfileLease Persistent(string family, IEnumerable<string> staleEndpointFiles)
    {
        var directory = Path.Combine(PersistentRoot(), family);
        System.IO.Directory.CreateDirectory(directory);
        // The launch runs with the ephemeral root as its working directory.
        System.IO.Directory.CreateDirectory(BrowserProfileDirectory.Root());
        FileStream handle;
        try
        {
            handle = new FileStream(Path.Combine(directory, LockFileName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new BrowserEngineException(
                "browser_profile_in_use",
                "Ru's persistent browser profile is in use by another Ru window; close the browser there or turn the persistent profile off.");
        }

        // A previous browser's endpoint file would point the new launch at a dead port.
        foreach (var name in staleEndpointFiles)
        {
            try
            {
                File.Delete(Path.Combine(directory, name));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                handle.Dispose();
                throw new BrowserEngineException("browser_profile_in_use", "Ru's persistent browser profile is still in use; try again shortly.");
            }
        }

        return new BrowserProfileLease(directory, handle);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_lock is not null)
        {
            await _lock.DisposeAsync().ConfigureAwait(false);
            return;
        }

        await BrowserProfileDirectory.DeleteAsync(Directory).ConfigureAwait(false);
    }
}
