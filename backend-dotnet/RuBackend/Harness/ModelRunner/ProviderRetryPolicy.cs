namespace RuBackend.Harness.ModelRunner;

/// <summary>
/// One bounded retry policy for a logical provider step; selection never changes. The budget
/// rides out a provider outage of about twelve minutes before the run fails with the provider's code: it
/// used to give up after about 37 seconds, so an unattended long run stopped on a short blip.
/// </summary>
internal static class ProviderRetryPolicy
{
    public const int MaximumAttempts = 10;
    public static readonly TimeSpan MaximumAutomaticDelay = TimeSpan.FromMinutes(5);

    public static bool CanRetry(bool retryable, TimeSpan? retryAfter, int attempt, bool cancelled) =>
        retryable && !cancelled && attempt >= 0 && attempt + 1 < MaximumAttempts
        && (retryAfter is null || retryAfter <= MaximumAutomaticDelay);

    public static TimeSpan Delay(TimeSpan backoff, TimeSpan? retryAfter, double jitterSample)
    {
        if (double.IsNaN(jitterSample) || jitterSample is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(jitterSample));
        var jitteredTicks = (long)(backoff.Ticks * (0.8 + 0.4 * jitterSample));
        var delay = TimeSpan.FromTicks(Math.Max(0, jitteredTicks));
        if (delay > MaximumAutomaticDelay) delay = MaximumAutomaticDelay;
        return retryAfter is { } serverDelay && serverDelay > delay ? serverDelay : delay;
    }
}
