using RuAgent.SessionInterchange;

namespace RuBackend.Configuration;

/// <summary>
/// The transport bound Kestrel itself enforces.
/// </summary>
/// <remarks>
/// Layering, outermost first: Kestrel's limit, then <see cref="RuBackend.Security.RequestSizeMiddleware"/>
/// per route, then the endpoint's own bounded reader. Kestrel must sit above every
/// application cap so an oversized request is refused by the layer that knows the route
/// and can answer with its own code. Kestrel's default (30,000,000 bytes) sat below the
/// import route's declared bound, which made the endpoint's <c>import_too_large</c> 413
/// unreachable on a real server; only a test server, which does not enforce Kestrel
/// limits, saw it.
/// </remarks>
public static class BackendLimits
{
    /// <summary>
    /// Kestrel's request body bound, a fixed margin above the deepest application cap
    /// (<see cref="SessionInterchangeLimits.MaxImportRouteBodyBytes"/>). The application
    /// caps stay the source of truth; this only keeps Kestrel from pre-empting them.
    /// </summary>
    public const long KestrelMaxRequestBodyBytes =
        SessionInterchangeLimits.MaxImportRouteBodyBytes + (4 * 1024 * 1024);
}
