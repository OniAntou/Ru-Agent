using RuAgent.Client;
using RuAgent.Updates;

namespace RuAgent.Cli;

public sealed record UpdateCliOptions(bool CheckOnly, bool Confirmed, UpdateChannel? Channel)
{
    public static UpdateCliOptions? Parse(string[] args)
    {
        bool check = false, yes = false;
        UpdateChannel? channel = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--check": if (check) return null; check = true; break;
                case "--yes": if (yes) return null; yes = true; break;
                case "--channel" when channel is null && i + 1 < args.Length:
                    channel = args[++i] switch { "stable" => UpdateChannel.Stable, "preview" => UpdateChannel.Preview, _ => null };
                    if (channel is null) return null;
                    break;
                default: return null;
            }
        }
        return new(check, yes, channel);
    }
}

public static class UpdateCommands
{
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var options = UpdateCliOptions.Parse(args);
        if (options is null)
        {
            await error.WriteLineAsync("Usage: ru update [--check] [--channel stable|preview] [--yes]");
            return 1;
        }
        try
        {
            var installed = InstalledRelease.Load(AppContext.BaseDirectory);
            if (installed is null) throw new UpdateException("update_installation_unknown: install a release package first; source builds use build.bat");
            var preferences = new UpdatePreferences();
            if (preferences.LoadLastResult() is { } last) await output.WriteLineAsync(last);
            var channel = options.Channel ?? preferences.LoadChannel();
            if (options.Channel is not null) preferences.SaveChannel(channel);
            await output.WriteLineAsync($"Ru {installed.Version} · {installed.Product} · {channel.ToString().ToLowerInvariant()}");
            using var client = new ReleaseUpdateClient();
            using var checkDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkDeadline.CancelAfter(TimeSpan.FromSeconds(30));
            var release = await client.CheckAsync(installed, channel, checkDeadline.Token).ConfigureAwait(false);
            if (release is null) { await output.WriteLineAsync("No newer compatible release is available on this channel."); return 0; }
            await output.WriteLineAsync($"Update available: {release.Version}\n{release.ReleaseUri}");
            if (options.CheckOnly) return 0;
            if (!options.Confirmed)
            {
                if (Console.IsInputRedirected) throw new UpdateException("update_confirmation_required: use --yes to install");
                await output.WriteAsync("Download and install this update? Ru will exit. [y/N] ");
                var answer = await Console.In.ReadLineAsync(ct).ConfigureAwait(false);
                if (answer?.Equals("y", StringComparison.OrdinalIgnoreCase) != true) return 0;
            }
            var staging = Path.Combine(preferences.DirectoryPath, Guid.NewGuid().ToString("N"));
            await output.WriteLineAsync("Downloading and verifying update…");
            using var downloadDeadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            downloadDeadline.CancelAfter(TimeSpan.FromMinutes(10));
            var downloaded = await client.DownloadAsync(release, staging, null, downloadDeadline.Token).ConfigureAwait(false);
            await UpdateMaintenance.PrepareAsync(ct).ConfigureAwait(false);
            await UpdateLauncher.StartAsync(downloaded, AppContext.BaseDirectory, preferences.DirectoryPath, restartDesktop: false, ct).ConfigureAwait(false);
            await output.WriteLineAsync("Verified. Installing after Ru exits. Run ru again after installation; details are in the update status file.");
            return 10; // The TUI uses this to exit and release its executable files.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await error.WriteLineAsync("Update cancelled."); return 130; }
        catch (OperationCanceledException) { await error.WriteLineAsync("Update timed out. Try again."); return 1; }
        catch (Exception ex) when (ex is UpdateException or HttpRequestException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            await error.WriteLineAsync($"Update failed: {ex.Message}");
            return 1;
        }
    }
}
