namespace RuAgent.Harness.Core;

/// <summary>
/// A model step the provider could not serve after the runner's bounded in-step retries
/// on a stable provider-transient code (rate limit, timeout, server or transport error),
/// or an account limit that needs operator action and is never retried. The coordinator
/// fails the run, root or child, with the provider's code; the user starts a new run once
/// the provider recovers. Carries the provider's code plus the original exception.
/// </summary>
public sealed class ProviderTransientPauseException : Exception
{
    public ProviderTransientPauseException(string code, Exception innerException)
        : base(code, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentNullException.ThrowIfNull(innerException);
        Code = code;
    }

    /// <summary>The provider's stable step-error code, safe for the transcript.</summary>
    public string Code { get; }

    /// <summary>
    /// The exact provider codes that are transient by origin: every one maps from
    /// a provider-reported recoverable condition in <c>ModelStepService</c> and
    /// is retryable in the runner. <c>protocol_failure</c> is deliberately absent:
    /// it is shared by deterministic protocol branches, so a code-only
    /// classification would park non-transient failures.
    /// </summary>
    public static bool IsProviderTransientCode(string? code) =>
        code is "provider_rate_limited"
            or "provider_timeout"
            or "provider_server_error"
            or "provider_transport_error";

    /// <summary>Provider codes that end the run with that code instead of a generic failure.</summary>
    public static bool EndsRunWithProviderCode(string? code) =>
        IsProviderTransientCode(code) || code == "provider_quota_exhausted";
}
