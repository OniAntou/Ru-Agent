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
    public static readonly TimeSpan WaveFrame = TimeSpan.FromMilliseconds(50);
    public static readonly TimeSpan ChatFlash = TimeSpan.FromMilliseconds(120);

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
