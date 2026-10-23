namespace RuApp.Services;

/// <summary>
/// Named UI polling/debounce/threshold constants (slice 3).
/// Values are the long-standing production intervals, centralized so timer
/// tuning happens in one place. Business logic unchanged.
/// </summary>
public static class UiTiming
{
    public static readonly TimeSpan SessionsPoll = TimeSpan.FromSeconds(3);
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

    /// <summary>
    /// Coalescing window for typing in the model picker.
    ///
    /// Each keystroke re-projected the catalog and rebuilt every row of the dropdown
    /// on the UI thread, so a burst of typing paid that cost once per character. Long
    /// enough to swallow a fast typist's whole word, short enough that the list still
    /// feels like it answered.
    /// </summary>
    public static readonly TimeSpan ModelSearchDebounce = TimeSpan.FromMilliseconds(180);

    /// <summary>
    /// Coalescing window for grouped catalog publishes.
    ///
    /// One poll cycle publishes once per provider (two hundred with Models.dev),
    /// and every publish re-projected all groups and rebuilt the picker diff on
    /// the UI thread. Collapsing the burst into one trailing apply keeps a poll
    /// at a single projection instead of hundreds.
    /// </summary>
    /// <summary>
    /// Coalescing window for grouped catalog publishes.
    ///
    /// One poll cycle publishes once per provider (two hundred with Models.dev),
    /// and every publish re-projected all groups and rebuilt the picker diff on
    /// the UI thread. Collapsing the burst into one trailing apply keeps a poll
    /// at a single projection instead of hundreds.
    /// </summary>
    public static readonly TimeSpan GroupedPublishCoalesce = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Coalescing window for the diagnostics filter boxes.
    ///
    /// Every keystroke re-scanned up to a thousand events and re-serialized
    /// each visible one for its detail pane. A fast typist's whole word
    /// collapses into one apply.
    /// </summary>
    public static readonly TimeSpan DiagnosticFilterDebounce = TimeSpan.FromMilliseconds(250);

    public static readonly TimeSpan ChatFlash = TimeSpan.FromMilliseconds(120);
    public const double ActivityWavePeriodSeconds = 0.65;
    public const double ChatFollowThresholdPx = 100.0;

    /// <summary>
    /// Minimum interval between streaming text renders. Every render re-parses the
    /// growing reply and rebuilds its runs, so a burst of deltas renders at most
    /// this often; the newest text always lands within one interval, including
    /// after the final delta, because the deferred render is scheduled, not dropped.
    /// </summary>
    public static readonly TimeSpan StreamTextRefresh = TimeSpan.FromMilliseconds(100);

    /// <summary>Fade-and-rise played when a reply block renders its first text.</summary>
    public const double ReplyTextArrivalMilliseconds = 140;


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
