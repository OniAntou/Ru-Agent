namespace RuApp.Services;

/// <summary>
/// Named UI polling/debounce/threshold constants (slice 3).
/// Values are the long-standing production intervals, centralized so timer
/// tuning happens in one place. Business logic unchanged.
/// </summary>
public static class UiTiming
{
    public static readonly TimeSpan TypingDebounce = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan SessionsPoll = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ObservationPoll = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReconnectPoll = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan ModelSaveFailure = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan AgentRetryDelay = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan WaveFrame = TimeSpan.FromMilliseconds(40);

    /// <summary>
    /// Coalescing window for a burst of activity frames.
    ///
    /// A run emits its frames in tight bursts: measured against a real run, the five
    /// frames of one tool call arrive about 47ms apart in total, while the gaps between
    /// calls run to many seconds. A window longer than the burst therefore held an
    /// entire call back and rendered it in one lump after the burst ended, which reads
    /// as the feed standing still and then jumping. This sits above one 60Hz frame so
    /// a burst still coalesces, and below the shortest burst so nothing waits for it.
    /// </summary>
    public static readonly TimeSpan ActivityFlushBatch = TimeSpan.FromMilliseconds(24);

    public static readonly TimeSpan ContextMeterDebounce = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan ChatFlash = TimeSpan.FromMilliseconds(120);
    public const double ActivityWavePeriodSeconds = 0.65;
    public const double ChatFollowThresholdPx = 100.0;

    public const double StaleReactionSeconds = 15.0;
    public const double ResizerActiveOpacity = 0.85;

    /// <summary>
    /// Mirrors the OS "show animations" setting (Settings -&gt; Accessibility -&gt;
    /// Visual effects). Decorative loops (welcome art, meteor field, pulses)
    /// must stay off when the user disables animations.
    /// </summary>
    public static bool AnimationsEnabled
    {
        get
        {
            try
            {
                return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }
}
