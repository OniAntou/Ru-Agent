namespace RuAgent.Harness.Core;

/// <summary>
/// A model step the runner reported with a stable provider-transient code after
/// its bounded in-step retries were exhausted (rate limit, timeout, server or
/// transport error). The provider may recover while the user waits, so the
/// coordinator parks the run for an explicit Continue instead of failing it.
/// Carries the provider's code plus the original exception.
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
}
