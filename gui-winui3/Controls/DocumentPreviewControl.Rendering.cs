using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using RuApp.Services;
using Windows.Data.Pdf;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace RuApp.Controls;

public sealed partial class DocumentPreviewControl
{
    private async Task RenderAsync(FilePreviewKind kind, byte[] bytes, CancellationToken ct, long generation)
    {
        PageImage.Visibility = CodeFrame.Visibility = MarkdownContent.Visibility = Visibility.Collapsed;
        PageToolbar.Visibility = ZoomToolbar.Visibility = Visibility.Collapsed;
        ContentScroll.Visibility = Visibility.Collapsed;
        bool code = kind == FilePreviewKind.Text;
        if (kind is FilePreviewKind.Text or FilePreviewKind.Markdown)
        {
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            string text = await reader.ReadToEndAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            if (text.Contains('\0')) throw new NotSupportedException("preview_binary_text: This file does not contain readable text.");
            if (FilePreviewSource.IsSvg(_path))
            {
                SvgSourceButton.Visibility = Visibility.Visible;
                SetSvgSourceButton();
                if (!_svgShowsSource && await ShowSvgPictureAsync(text, ct, generation)) code = false;
                else if (generation == _generation) ShowCode(text);
            }
            else if (kind == FilePreviewKind.Markdown)
            {
                MarkdownContent.MarkdownText = text;
                MarkdownContent.Visibility = Visibility.Visible;
                ShowDetail("Markdown", PreviewDetail.Lines(LineCount(text)));
            }
            else ShowCode(text);
        }
        else if (kind == FilePreviewKind.Pdf)
        {
            var stream = new MemoryStream(bytes).AsRandomAccessStream();
            try
            {
                var document = await PdfDocument.LoadFromStreamAsync(stream).AsTask(ct);
                ct.ThrowIfCancellationRequested();
                if (generation != _generation) { stream.Dispose(); return; }
                _pdfStream = stream; _pdf = document; _page = 0;
                await RenderPdfPageAsync(ct, generation);
                if (generation != _generation) return;
                ShowDetail("PDF", PreviewDetail.Pages((int)document.PageCount));
            }
            catch { if (!ReferenceEquals(stream, _pdfStream)) stream.Dispose(); throw; }
        }
        else
        {
            using var stream = new MemoryStream(bytes).AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
            if ((ulong)decoder.PixelWidth * decoder.PixelHeight > 40_000_000) throw new NotSupportedException("preview_image_too_large: Image exceeds 40 megapixels.");
            var image = new BitmapImage { DecodePixelWidth = 2048 };
            stream.Seek(0);
            await image.SetSourceAsync(stream).AsTask(ct);
            ct.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            PageImage.Source = image; PageImage.Visibility = Visibility.Visible;
            ZoomToolbar.Visibility = Visibility.Visible;
            ShowDetail(ImageTypeName(_path), PreviewDetail.Dimensions(
                (int)Math.Min(decoder.PixelWidth, int.MaxValue), (int)Math.Min(decoder.PixelHeight, int.MaxValue)));
        }
        if (generation != _generation) return;
        FitPreviewButton.Visibility = Visibility.Visible;
        StatusLabel.Visibility = Visibility.Collapsed;
        if (code)
        {
            // The code pane scrolls in its own columns; the page scroller stays collapsed.
            CodeScroll.UpdateLayout();
            CodeScroll.ChangeView(0, 0, null, true);
            SyncCodeGutter();
            return;
        }

        ContentScroll.Visibility = Visibility.Visible;
        ContentScroll.UpdateLayout();
        ContentScroll.ChangeView(0, 0, 1, true);
        RefreshZoom();
    }

    private bool _svgShowsSource;

    private void SetSvgSourceButton()
    {
        SvgSourceButton.Content = _svgShowsSource ? "Picture" : "Source";
        string name = _svgShowsSource ? "Show the SVG as a picture" : "Show the SVG's source text";
        ToolTipService.SetToolTip(SvgSourceButton, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SvgSourceButton, name);
    }

    /// <summary>
    /// Draws the SVG through the sandboxed renderer: as an image it runs no script and
    /// loads nothing. When the renderer cannot draw it, the source shows instead.
    /// </summary>
    private async Task<bool> ShowSvgPictureAsync(string text, CancellationToken ct, long generation)
    {
        var style = new SandboxRenderStyle(ActualTheme == ElementTheme.Dark, "#000000", 14);
        var picture = await SandboxRenderer.Shared.RenderAsync(SandboxRenderKind.Svg, text, style, ct);
        ct.ThrowIfCancellationRequested();
        if (picture is null || generation != _generation) return false;
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(picture.Png.AsBuffer()).AsTask(ct);
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream).AsTask(ct);
        if (generation != _generation) return false;
        PageImage.Source = image; PageImage.Visibility = Visibility.Visible;
        ZoomToolbar.Visibility = Visibility.Visible;
        ShowDetail("SVG", PreviewDetail.Dimensions((int)Math.Round(picture.Width), (int)Math.Round(picture.Height)));
        return true;
    }

    /// <summary>Fills the code frame: line numbers, then the highlighted lines.</summary>
    private void ShowCode(string text)
    {
        var view = CodePreviewModel.Build(text, _path);
        LineNumbers.Text = view.LineNumbers;
        TextContent.Inlines.Clear();
        for (int index = 0; index < view.Lines.Count; index++)
        {
            if (index > 0) TextContent.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
            foreach (var span in view.Lines[index]) TextContent.Inlines.Add(CodeTheme.Run(span));
        }

        ShowDetail(view.Description);
        CodeFrame.Visibility = Visibility.Visible;
    }

    private async Task RenderPdfPageAsync(CancellationToken ct, long generation)
    {
        if (_pdf is not { PageCount: > 0 } document) throw new IOException("preview_pdf_empty");
        using var page = document.GetPage(_page);
        using var pixels = new InMemoryRandomAccessStream();
        double scale = Math.Min(1600 / page.Size.Width, 2400 / page.Size.Height);
        if (!double.IsFinite(scale) || scale <= 0) throw new IOException("preview_pdf_invalid_page");
        await page.RenderToStreamAsync(pixels, new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Clamp(page.Size.Width * scale, 1, 1600),
            DestinationHeight = (uint)Math.Clamp(page.Size.Height * scale, 1, 2400),
        }).AsTask(ct);
        var image = new BitmapImage();
        pixels.Seek(0);
        await image.SetSourceAsync(pixels).AsTask(ct);
        ct.ThrowIfCancellationRequested();
        if (generation != _generation) return;
        PageImage.Source = image; PageImage.Visibility = Visibility.Visible;
        // One page needs no pager; a longer document shows where the reader is.
        PageToolbar.Visibility = document.PageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
        ZoomToolbar.Visibility = Visibility.Visible;
        PageTotalLabel.Text = "of " + PreviewDetail.Pages((int)document.PageCount);
        SyncPageInput();
        RefreshPageControls();
        ContentScroll.ChangeView(0, 0, null, true);
    }

    /// <summary>Lines the way the reader counts them: a final newline closes the last one.</summary>
    private static int LineCount(string text)
    {
        if (text.Length == 0) return 0;
        int lines = 1;
        foreach (char character in text)
        {
            if (character == '\n') lines++;
        }

        return text.EndsWith('\n') ? lines - 1 : lines;
    }

    private static string ImageTypeName(string? path)
    {
        string name = System.IO.Path.GetExtension(path ?? string.Empty).TrimStart('.').ToUpperInvariant();
        return name switch { "JPG" => "JPEG", "TIF" => "TIFF", _ => name };
    }
}
