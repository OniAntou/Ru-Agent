namespace RuAgent.Harness.Core;

/// <summary>
/// A model step that needs an explicit Continue: exhausted transient retries
/// (rate limit, timeout, server or transport error), or an account limit that
/// needs operator action without automatic retries. The coordinator parks the
/// run instead of failing it; the same provider/model selection stays pinned.
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

    /// <summary>Account limits can be repaired by the operator, but never auto-retried.</summary>
    public static bool RequiresExplicitContinue(string? code) =>
        IsProviderTransientCode(code) || code == "provider_quota_exhausted";
}
