using System.Diagnostics;
using RuAgent.Broker.Tools.Terminal;

namespace RuAgent.Broker.Tools.Browser;

/// <summary>
/// A Chromium browser process started for the tool with a random DevTools port (ADR 0055).
/// Disposal kills the process tree and releases the profile: an ephemeral profile is deleted, so
/// no cookie survives the run; Ru's persistent profile (amendment 2026-11-25) is kept.
/// </summary>
internal sealed class CdpBrowserProcess : IAsyncDisposable
{
    private readonly Process _process;
    private readonly BrowserProfileLease _profile;
    private readonly WindowsJobProcessTree? _job;
    private int _disposed;

    private CdpBrowserProcess(Process process, BrowserProfileLease profile, int port, Uri webSocketUri, WindowsJobProcessTree? job)
    {
        _process = process;
        _profile = profile;
        _job = job;
        Port = port;
        WebSocketUri = webSocketUri;
    }

    public int Port { get; }

    public Uri WebSocketUri { get; }

    public static async Task<CdpBrowserProcess> StartAsync(
        string executable,
        bool headless,
        bool persistentProfile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var profile = persistentProfile
            ? BrowserProfileLease.Persistent("chromium", ["DevToolsActivePort"])
            : BrowserProfileLease.Ephemeral();

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = BrowserProfileDirectory.Root(),
        };
        startInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        startInfo.ArgumentList.Add("--remote-debugging-port=0");
        startInfo.ArgumentList.Add("--user-data-dir=" + profile.Directory);
        startInfo.ArgumentList.Add("--no-first-run");
        startInfo.ArgumentList.Add("--no-default-browser-check");
        startInfo.ArgumentList.Add("--disable-extensions");
        startInfo.ArgumentList.Add("--disable-sync");
        startInfo.ArgumentList.Add("--disable-background-networking");
        startInfo.ArgumentList.Add("--disable-component-update");
        startInfo.ArgumentList.Add("--disable-default-apps");
        startInfo.ArgumentList.Add("--disable-features=Translate,MediaRouter,OptimizationHints");
        startInfo.ArgumentList.Add("--metrics-recording-only");
        startInfo.ArgumentList.Add("--window-size=" + RuAgent.Protocol.BrowserLimits.MaxPageWidth + "," + RuAgent.Protocol.BrowserLimits.MaxPageHeight);
        if (headless) startInfo.ArgumentList.Add("--headless=new");
        startInfo.ArgumentList.Add("about:blank");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            await profile.DisposeAsync().ConfigureAwait(false);
            throw new BrowserEngineException(
                "browser_launch_failed",
                "The browser could not start: " + Bound(error.Message));
        }

        if (process is null)
        {
            await profile.DisposeAsync().ConfigureAwait(false);
            throw new BrowserEngineException("browser_launch_failed", "The browser process did not start.");
        }

        // Draining both pipes keeps a chatty browser from blocking on a full pipe.
        _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        _ = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var job = BrowserProcessJob.TryAttach(process);
        try
        {
            var (port, path) = await WaitForDevToolsAsync(process, profile.Directory, cancellationToken).ConfigureAwait(false);
            return new CdpBrowserProcess(process, profile, port, new Uri("ws://127.0.0.1:" + port + path), job);
        }
        catch (Exception)
        {
            BrowserProcessJob.Release(job);
            Kill(process);
            await profile.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_profile.IsPersistent)
        {
            // The engine asked the browser to close; letting it exit writes the persistent
            // profile's cookies, which a kill could lose.
            try
            {
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _process.WaitForExitAsync(exit.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
            {
                // It did not exit in time; the kill below ends it.
            }
        }

        BrowserProcessJob.Release(_job);
        Kill(_process);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException)
        {
            // The process tree was already killed; a stuck exit handle must not fail disposal.
        }

        _process.Dispose();
        await _profile.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<(int Port, string Path)> WaitForDevToolsAsync(
        Process process,
        string profile,
        CancellationToken cancellationToken)
    {
        var activePort = Path.Combine(profile, "DevToolsActivePort");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new BrowserEngineException(
                    "browser_launch_failed",
                    "The browser exited during startup with code " + process.ExitCode + ".");
            }

            if (File.Exists(activePort))
            {
                string[] lines;
                try
                {
                    lines = await File.ReadAllLinesAsync(activePort, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (lines.Length >= 2
                    && int.TryParse(lines[0].Trim(), out var port)
                    && port is > 0 and <= 65535
                    && lines[1].TrimStart().StartsWith("/devtools/browser/", StringComparison.Ordinal))
                {
                    return (port, lines[1].Trim());
                }
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        throw new BrowserEngineException(
            "browser_launch_failed",
            "The browser did not open its DevTools port within 20 seconds.");
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Best effort: the profile cleanup below still runs.
        }
    }

    private static string Bound(string text) => text.Length <= 256 ? text : text[..256];
}
