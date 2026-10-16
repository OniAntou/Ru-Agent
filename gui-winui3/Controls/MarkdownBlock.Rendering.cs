using Markdig.Syntax;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;

namespace RuApp.Controls;

public sealed partial class MarkdownBlock
{
    private sealed record RenderedBlock(MarkdownRenderKey Key, Paragraph[] Paragraphs,
        List<(FrameworkElement Element, double Inset)> Embedded);

    private readonly List<RenderedBlock> _renderedBlocks = [];

    private void UpdateRenderedBlocks(string source, double bodySize)
    {
        var document = MarkdownEngine.Parse(source);
        var keys = MarkdownRenderPlan.Keys(document, source);
        int retained = Math.Abs(bodySize - _lastRenderedSize) < 0.01
            ? MarkdownRenderPlan.RetainedPrefix(_renderedBlocks.Select(row => row.Key).ToArray(), keys) : 0;
        var staged = new List<RenderedBlock>();
        // Construct the changed suffix before touching the visible tree. Each
        // paragraph/InlineUIContainer keeps exactly one native parent.
        for (int index = retained; index < document.Count; index++)
        {
            var embedded = new List<(FrameworkElement Element, double Inset)>();
            var renderer = new BlockRenderer(bodySize, Foreground,
                (element, inset) => FitEmbeddedBlock(element, inset, embedded));
            var paragraphs = renderer.BuildBlocks(document[index], 0).ToArray();
            staged.Add(new(keys[index], paragraphs, embedded));
        }

        int paragraphCount = _renderedBlocks.Take(retained).Sum(row => row.Paragraphs.Length);
        while (_richTextBlock.Blocks.Count > paragraphCount)
            _richTextBlock.Blocks.RemoveAt(_richTextBlock.Blocks.Count - 1);
        _renderedBlocks.RemoveRange(retained, _renderedBlocks.Count - retained);
        foreach (var row in staged)
        {
            foreach (var paragraph in row.Paragraphs) _richTextBlock.Blocks.Add(paragraph);
            _renderedBlocks.Add(row);
        }
        _embeddedBlocks.Clear();
        foreach (var row in _renderedBlocks) _embeddedBlocks.AddRange(row.Embedded);
    }
}
