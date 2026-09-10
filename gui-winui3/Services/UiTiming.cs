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
    public static readonly TimeSpan WaveFrame = TimeSpan.FromMilliseconds(33);
    public static readonly TimeSpan ChatFlash = TimeSpan.FromMilliseconds(120);

    public const double StaleReactionSeconds = 15.0;
    public const double ResizerActiveOpacity = 0.85;
}
