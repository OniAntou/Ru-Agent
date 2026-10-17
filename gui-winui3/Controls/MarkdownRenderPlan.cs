using System.Text.Json;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace RuApp.Controls;

/// <summary>Identifies the unchanged block prefix, including resolved reference links.</summary>
internal sealed record MarkdownRenderKey(Type Kind, string Source, string Links);

internal static class MarkdownRenderPlan
{
    internal static string PrepareSource(string value)
    {
        var lines = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var recovered = new List<string>(lines.Length);
        char fence = '\0';
        int fenceLength = 0;
        foreach (var line in lines)
        {
            var start = line.TrimStart();
            int run = 0;
            if (start.Length > 0 && start[0] is '`' or '~')
                while (run < start.Length && start[run] == start[0]) run++;
            if (fence != '\0')
            {
                recovered.Add(line);
                if (run >= fenceLength && start[0] == fence && string.IsNullOrWhiteSpace(start[run..])) fence = '\0';
            }
            else if (run >= 3)
            {
                fence = start[0];
                fenceLength = run;
                recovered.Add(line);
            }
            else if (line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t')) recovered.Add(line);
            else recovered.AddRange(Services.MarkdownRecovery.SplitLine(line));
        }
        return string.Join('\n', recovered);
    }

    internal static IReadOnlyList<MarkdownRenderKey> Keys(MarkdownDocument document, string source) =>
        document.Select(block => new MarkdownRenderKey(block.GetType(),
            block.Span.Start >= 0 && block.Span.End >= block.Span.Start && block.Span.End < source.Length
                ? source.Substring(block.Span.Start, block.Span.Length) : string.Empty,
            JsonSerializer.Serialize(Links(block)))).ToArray();

    private static IEnumerable<string?> Links(Block block)
    {
        IEnumerable<LeafBlock> leaves = block is LeafBlock leaf ? [leaf] : block.Descendants().OfType<LeafBlock>();
        foreach (var item in leaves)
        {
            if (item.Inline is null) continue;
            foreach (var target in Links(item.Inline)) yield return target;
        }
    }

    private static IEnumerable<string?> Links(ContainerInline parent)
    {
        foreach (var inline in parent)
        {
            if (inline is LinkInline link) { yield return link.Url; yield return link.Title; }
            if (inline is ContainerInline nested)
                foreach (var target in Links(nested)) yield return target;
        }
    }

    internal static int RetainedPrefix(IReadOnlyList<MarkdownRenderKey> previous, IReadOnlyList<MarkdownRenderKey> next)
    {
        int count = 0;
        while (count < previous.Count && count < next.Count && previous[count] == next[count]) count++;
        return count;
    }
}
