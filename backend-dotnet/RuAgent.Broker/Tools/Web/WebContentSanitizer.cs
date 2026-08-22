using System.Net;
using System.Text.RegularExpressions;

namespace RuAgent.Broker.Tools.Web;

public sealed class WebContentSanitizer
{
    private static readonly Regex BlockTags = new("<(script|style|noscript|iframe|object|embed)[^>]*>.*?</\\1>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Tags = new("<[^>]+>", RegexOptions.Compiled);

    public string Sanitize(string sourceTitle, Uri sourceUrl, string html, int maxCharacters = 32_000)
    {
        ArgumentNullException.ThrowIfNull(sourceUrl);
        if (maxCharacters is < 1 or > 256_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        }

        var content = BlockTags.Replace(html ?? string.Empty, " ");
        content = WebUtility.HtmlDecode(Tags.Replace(content, " "));
        content = Regex.Replace(content, @"\s+", " ").Trim();
        if (content.Length > maxCharacters)
        {
            content = content[..maxCharacters].TrimEnd() + "...[truncated]";
        }

        return $"[untrusted_web_reference]\nsource_title: {Bound(sourceTitle)}\nsource_url: {sourceUrl}\ncontent: {content}\n[/untrusted_web_reference]";
    }

    private static string Bound(string value) =>
        string.IsNullOrWhiteSpace(value) ? "Untitled source" : value.Trim()[..Math.Min(value.Trim().Length, 256)];
}
