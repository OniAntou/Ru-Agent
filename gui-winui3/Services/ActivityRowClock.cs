using Microsoft.UI.Dispatching;

namespace RuApp.Services;

/// <summary>
/// One once-a-second tick shared by every running activity row's clock, so a feed of
/// running rows costs one timer, and none at all once nothing runs. It changes text
/// only, so it keeps ticking when Windows animations are off.
/// </summary>
internal static class ActivityRowClock
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private static readonly HashSet<Action> Subscribers = new();
    private static DispatcherQueueTimer? _timer;

    /// <summary>Adds <paramref name="tick"/>; call on the UI thread.</summary>
    public static void Subscribe(Action tick)
    {
        ArgumentNullException.ThrowIfNull(tick);
        if (!Subscribers.Add(tick)) return;
        if (_timer is null)
        {
            if (DispatcherQueue.GetForCurrentThread() is not { } queue) return;
            _timer = queue.CreateTimer();
            _timer.Interval = Interval;
            _timer.Tick += OnTick;
        }

        if (!_timer.IsRunning) _timer.Start();
    }

    public static void Unsubscribe(Action tick)
    {
        if (!Subscribers.Remove(tick) || Subscribers.Count > 0) return;
        _timer?.Stop();
    }

    private static void OnTick(DispatcherQueueTimer sender, object args)
    {
        // A subscriber may unsubscribe itself from its own tick.
        foreach (var tick in Subscribers.ToArray()) tick();
        if (Subscribers.Count == 0) sender.Stop();
    }
}
