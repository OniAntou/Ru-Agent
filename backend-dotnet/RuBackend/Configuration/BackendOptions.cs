using RuAgent.SessionInterchange;

namespace RuBackend.Configuration;

public sealed class BackendOptions
{
    public const string SectionName = "RuBackend";

    public string BindUrl { get; set; } = "http://127.0.0.1:8010";

    public string DesktopToken { get; set; } = string.Empty;

    public long MaxRequestBodyBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// Body bound for <c>POST /agent/v1/runs</c>, which may carry up to four image
    /// data URLs (up to ~5.6M characters each) plus the JSON envelope. The generic
    /// <see cref="MaxRequestBodyBytes"/> stays small for every other route.
    /// </summary>
    public long AgentRunMaxRequestBodyBytes { get; set; } = 26 * 1024 * 1024;

    /// <summary>
    /// Declared-body bound for <c>POST /sessions/import</c>, set just above the
    /// endpoint's own <see cref="SessionInterchangeLimits.MaxImportBodyBytes"/> so the
    /// endpoint answers oversized imports with <c>import_too_large</c>. Kestrel's
    /// transport bound sits above this one (<see cref="BackendLimits.KestrelMaxRequestBodyBytes"/>).
    /// </summary>
    public long SessionImportMaxRequestBodyBytes { get; set; } = SessionInterchangeLimits.MaxImportRouteBodyBytes;
}
