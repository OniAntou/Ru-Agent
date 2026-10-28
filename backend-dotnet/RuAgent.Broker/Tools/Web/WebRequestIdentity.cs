using System.Reflection;

namespace RuAgent.Broker.Tools.Web;

/// <summary>
/// How <c>web_fetch</c> introduces itself to a site.
/// </summary>
/// <remarks>
/// A request with no User-Agent or Accept header is refused by many ordinary sites
/// (Wikipedia answers 403, others 406). Ru names itself honestly instead of posing as
/// a browser: a site that turns away automated clients stays turned away, and the tool
/// reports that as a named failure rather than working around it.
/// </remarks>
internal static class WebRequestIdentity
{
    public const string ProjectUrl = "https://github.com/OniAntou/Ru-Agent";

    public static readonly string UserAgent = $"RuAgent/{Version()} (+{ProjectUrl})";

    public const string Accept =
        "text/html,application/xhtml+xml,application/xml;q=0.9,application/json;q=0.9,text/plain;q=0.8,application/pdf;q=0.7,*/*;q=0.5";

    public const string AcceptLanguage = "en-US,en;q=0.9,vi;q=0.8";

    public static void Apply(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", Accept);
        request.Headers.TryAddWithoutValidation("Accept-Language", AcceptLanguage);
    }

    private static string Version()
    {
        var informational = typeof(WebRequestIdentity).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // Drop source-link build metadata ("1.2.3+abcdef"); it is noise in a header.
        var version = informational?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version)
            ? typeof(WebRequestIdentity).Assembly.GetName().Version?.ToString(3) ?? "0"
            : version;
    }
}
