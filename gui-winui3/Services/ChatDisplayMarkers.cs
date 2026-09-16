using System.Text.RegularExpressions;

namespace RuApp.Services;

/// <summary>
/// Product markers that are machine metadata and must never reach the transcript.
/// The pattern belongs here rather than in the view so the strip rule has one owner.
/// </summary>
public static partial class ChatDisplayMarkers
{
    /// <summary>
    /// A legacy in-band marker emitted by the retired VTS and voice surfaces. Such
    /// runs no longer exist, but stored sessions still contain them and they must not
    /// render as visible text.
    /// </summary>
    public const string Pattern = @"\s*\[\[(?:VTS|VOICE_PACE):[^\]]*\]\]";

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex { get; }

    public static string Strip(string? content) =>
        string.IsNullOrEmpty(content) ? string.Empty : MarkerRegex.Replace(content, string.Empty).Trim();
}
