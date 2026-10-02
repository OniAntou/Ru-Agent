using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RuAgent.Hosting;

/// <summary>
/// Stops the managed host after a configured idle window: no request in flight, nothing
/// served for the whole window and no registered run. Clients restart it on demand —
/// CLI and GUI both go through <c>PublicHostStarter.GetOrStartAsync</c> — so closing the
/// terminal no longer leaves a detached backend running forever.
/// <para>
/// The window comes from <c>RU_HOST_IDLE_MINUTES</c> (default 10, <c>0</c> disables).
/// </para>
/// </summary>
public sealed class HostIdleShutdownService : BackgroundService
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(30);
    private const double DefaultIdleMinutes = 10;

    private readonly IHostApplicationLifetime _lifetime;
    private readonly HostActivityTracker _activity;
    private readonly IHostIdleProbe? _probe;
    private readonly TimeProvider _clock;
    private readonly ILogger<HostIdleShutdownService> _logger;
    private readonly TimeSpan _window;
    private readonly TimeSpan _pollInterval;

    public HostIdleShutdownService(
        IHostApplicationLifetime lifetime,
        HostActivityTracker activity,
        ILogger<HostIdleShutdownService> logger,
        TimeProvider clock,
        IHostIdleProbe? probe = null,
        TimeSpan? window = null,
        TimeSpan? pollInterval = null)
    {
        _lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        _activity = activity ?? throw new ArgumentNullException(nameof(activity));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _probe = probe;
        _window = window ?? ResolveWindow(Environment.GetEnvironmentVariable("RU_HOST_IDLE_MINUTES"));
        _pollInterval = pollInterval ?? DefaultPollInterval;
    }

    /// <summary>
    /// Resolves the idle window from a minutes string: a missing or malformed value keeps
    /// the 10-minute default, zero disables the shutdown, a negative value falls back.
    /// </summary>
    public static TimeSpan ResolveWindow(string? rawMinutes)
    {
        if (string.IsNullOrWhiteSpace(rawMinutes))
        {
            return TimeSpan.FromMinutes(DefaultIdleMinutes);
        }

        if (!double.TryParse(rawMinutes.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes))
        {
            return TimeSpan.FromMinutes(DefaultIdleMinutes);
        }

        if (minutes < 0)
        {
            return TimeSpan.FromMinutes(DefaultIdleMinutes);
        }

        return minutes == 0 ? TimeSpan.Zero : TimeSpan.FromMinutes(minutes);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_window <= TimeSpan.Zero)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var now = _clock.GetUtcNow();
            var activeWork = _probe?.ActiveWorkCount ?? 0;
            if (HostActivityTracker.ShouldShutdown(now, _activity.LastActivityUtc, _activity.OpenRequests, activeWork, _window))
            {
                _logger.LogInformation(
                    "managed host idle shutdown: no requests and no active runs for {Minutes:0.#} minutes",
                    _window.TotalMinutes);
                _lifetime.StopApplication();
                return;
            }
        }
    }
}
