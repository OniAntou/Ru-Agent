using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using RuAgent.Broker.Configuration;

namespace RuAgent.Broker.Tools.Web;

public sealed class WebContentSanitizer
{
    public const int DefaultMaxCharacters = 32_000;
    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "p", "div", "section", "article", "main", "h1", "h2", "h3", "h4", "h5", "h6",
        "ul", "ol", "li", "table", "tr", "blockquote", "dl", "dt", "dd", "hr",
    };

    public string Sanitize(string sourceTitle, Uri sourceUrl, string html, int maxCharacters = DefaultMaxCharacters) =>
        Extract(sourceTitle, sourceUrl, html, true, maxCharacters).Text;

    public (string Title, string Text, int? NextOffset) Extract(string sourceTitle, Uri sourceUrl, string text, bool isHtml,
        int maxCharacters = DefaultMaxCharacters, int offset = 0)
    {
        ArgumentNullException.ThrowIfNull(sourceUrl);
        if (maxCharacters is < 1 or > 256_000)
            throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        if (offset is < 0 or > 4 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(offset));

        string content;
        if (isHtml)
        {
            using var document = new HtmlParser().ParseDocument(text);
            if (document.QuerySelector("#challenge-form,#cf-challenge-running,form[action*='/anomaly.js']") is not null)
                throw new InvalidDataException("web_page_blocked");
            sourceTitle = document.QuerySelector("title")?.TextContent ?? sourceTitle;
            foreach (var node in document.QuerySelectorAll("script,style,noscript,iframe,object,embed,nav,footer,aside,[hidden],[aria-hidden='true']"))
                node.Remove();
            var root = document.QuerySelector("main,article,[role='main']") ?? document.Body;
            var writer = new StringBuilder();
            if (root is not null) Write(root, writer, WebOptions.MaxConfiguredResponseBytes, sourceUrl);
            if (writer.Length > WebOptions.MaxConfiguredResponseBytes)
                throw new InvalidDataException("web_extracted_content_too_large");
            content = writer.ToString().TrimStart('\n').TrimEnd();
        }
        else
        {
            // Markdown, JSON and code are already text: retain <T> and indentation.
            content = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n');
        }

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("web_content_empty");
        if (offset > content.Length)
            throw new InvalidDataException("web_offset_out_of_range");
        if (offset > 0 && offset < content.Length && char.IsHighSurrogate(content[offset - 1]) && char.IsLowSurrogate(content[offset]))
            throw new InvalidDataException("web_offset_invalid_boundary");
        var count = Math.Min(maxCharacters, content.Length - offset);
        if (count > 0 && offset + count < content.Length
            && char.IsHighSurrogate(content[offset + count - 1]) && char.IsLowSurrogate(content[offset + count]))
            count--;
        if (count == 0 && offset < content.Length)
            throw new InvalidDataException("web_limit_too_small");
        int? nextOffset = offset + count < content.Length ? offset + count : null;
        content = content.Substring(offset, count);
        if (nextOffset is not null) content += "...[truncated]";
        var title = string.Join(' ', (sourceTitle ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        title = string.IsNullOrEmpty(title) ? "Untitled source" : title[..Math.Min(title.Length, 256)];
        return (title, $"[untrusted_web_reference]\nsource_title: {title}\nsource_url: {sourceUrl}\ncontent: {content}\n[/untrusted_web_reference]", nextOffset);
    }

    private static void Write(INode root, StringBuilder writer, int limit, Uri sourceUrl)
    {
        // Explicit stack handles deeply nested input without recursive overflow.
        var pending = new Stack<(INode Node, bool Closing)>();
        pending.Push((root, false));
        while (pending.Count > 0 && writer.Length <= limit)
        {
            var (node, closing) = pending.Pop();
            if (node is IText text)
            {
                foreach (var character in text.Data)
                {
                    if (writer.Length > limit) break;
                    if (!char.IsWhiteSpace(character)) writer.Append(character);
                    else if (writer.Length > 0 && !char.IsWhiteSpace(writer[^1])) writer.Append(' ');
                }
                continue;
            }
            if (node is not IElement element) continue;
            if (element.LocalName == "a" && Uri.TryCreate(sourceUrl, element.GetAttribute("href"), out var link)
                && link.Scheme is "http" or "https")
            {
                var label = string.Join(' ', element.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                var reference = $"[{label}]({link})";
                writer.Append(reference.AsSpan(0, Math.Min(reference.Length, Math.Max(0, limit + 1 - writer.Length))));
                continue;
            }
            if (element.LocalName == "pre")
            {
                Break(writer);
                var code = element.TextContent.Replace("\r\n", "\n", StringComparison.Ordinal);
                writer.Append(code.AsSpan(0, Math.Min(code.Length, Math.Max(0, limit + 1 - writer.Length))));
                Break(writer);
                continue;
            }
            if (element.LocalName == "br" || BlockElements.Contains(element.LocalName)) Break(writer);
            if (closing) continue;
            if (element.LocalName is "td" or "th") writer.Append(" | ");
            pending.Push((node, true));
            for (var index = node.ChildNodes.Length - 1; index >= 0; index--)
                pending.Push((node.ChildNodes[index], false));
        }
    }

    private static void Break(StringBuilder writer)
    {
        while (writer.Length > 0 && writer[^1] == ' ') writer.Length--;
        if (writer.Length > 0 && writer[^1] != '\n') writer.Append('\n');
    }
}
