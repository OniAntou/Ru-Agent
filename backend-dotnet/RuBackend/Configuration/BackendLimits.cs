namespace RuBackend.Configuration;

/// <summary>
/// The transport bound Kestrel itself enforces.
/// </summary>
/// <remarks>
/// Kestrel's transport bound sits above the largest application request bound.
/// The application bound is configuration (<see cref="BackendOptions.AgentRunMaxRequestBodyBytes"/>),
/// so the transport bound is derived from it at startup; a fixed constant would
/// make Kestrel answer a generic 413 before the run route could return its own
/// bounded error code whenever an operator raised the configured cap.
/// </remarks>
public static class BackendLimits
{
    /// <summary>Room the transport bound keeps above the largest application cap.</summary>
    public const long TransportMarginBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Kestrel's default request body bound, a fixed margin above the default
    /// application cap.
    /// </summary>
    public const long KestrelMaxRequestBodyBytes =
        (26 * 1024 * 1024) + TransportMarginBytes;

    /// <summary>
    /// The transport bound for the largest configured application body cap: the
    /// default bound, raised when the configuration asks for a larger one.
    /// </summary>
    public static long KestrelMaxRequestBodyBytesFor(long maxApplicationBodyBytes)
    {
        if (maxApplicationBodyBytes <= 0)
        {
            return KestrelMaxRequestBodyBytes;
        }

        return maxApplicationBodyBytes <= long.MaxValue - TransportMarginBytes
            ? Math.Max(KestrelMaxRequestBodyBytes, maxApplicationBodyBytes + TransportMarginBytes)
            : long.MaxValue;
    }
}
