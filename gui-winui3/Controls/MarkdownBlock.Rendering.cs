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
        var style = PictureStyle(bodySize);
        for (int index = retained; index < document.Count; index++)
        {
            var embedded = new List<(FrameworkElement Element, double Inset)>();
            var pictures = new MarkdownPictureContext(
                MarkdownPictures.IsSettled(index, document.Count, IsStreaming), style);
            var renderer = new BlockRenderer(bodySize, Foreground,
                (element, inset) => FitEmbeddedBlock(element, inset, embedded), OpenLocalLink, pictures);
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

    /// <summary>Pictures take the reader's theme, text color and size.</summary>
    private Services.SandboxRenderStyle PictureStyle(double bodySize)
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        string ink = Foreground is Microsoft.UI.Xaml.Media.SolidColorBrush { Color: var color } && color.A == 255
            ? MarkdownPictures.Ink(color.R, color.G, color.B)
            : dark ? "#e5e7eb" : "#0f172a";
        return new Services.SandboxRenderStyle(dark, ink, bodySize);
    }

    /// <summary>
    /// Rebuilds the last block once a reply stops streaming, so the diagram or formula
    /// that was still arriving is drawn; settled blocks above it are kept as they are.
    /// </summary>
    private void SettleStreamingTail()
    {
        if (_renderedBlocks.Count == 0 || _lastRenderedText is not { } source || !MarkdownPictures.MayHold(source)) return;
        _renderedBlocks.RemoveAt(_renderedBlocks.Count - 1);
        _lastRenderedText = null;
        RenderMarkdown();
    }

    /// <summary>A theme change redraws every picture in the new colors.</summary>
    private void RedrawForTheme()
    {
        if (_lastRenderedText is not { } source || !MarkdownPictures.MayHold(source)) return;
        _renderedBlocks.Clear();
        _lastRenderedText = null;
        RenderMarkdown();
    }
}
