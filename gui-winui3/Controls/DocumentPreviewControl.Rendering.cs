using Microsoft.UI.Xaml;
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
        PageImage.Visibility = TextContent.Visibility = MarkdownContent.Visibility = Visibility.Collapsed;
        if (kind is FilePreviewKind.Text or FilePreviewKind.Markdown)
        {
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            string text = await reader.ReadToEndAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            if (text.Contains('\0')) throw new NotSupportedException("preview_binary_text: This file does not contain readable text.");
            if (kind == FilePreviewKind.Markdown) { MarkdownContent.MarkdownText = text; MarkdownContent.Visibility = Visibility.Visible; }
            else { TextContent.Text = text; TextContent.Visibility = Visibility.Visible; }
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
        }
        if (generation != _generation) return;
        ContentScroll.Visibility = Visibility.Visible;
        FitPreviewButton.Visibility = Visibility.Visible;
        ContentScroll.UpdateLayout();
        ContentScroll.ChangeView(0, 0, 1, true);
        StatusLabel.Visibility = Visibility.Collapsed;
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
        PageToolbar.Visibility = Visibility.Visible;
        PageLabel.Text = $"Page {_page + 1} of {document.PageCount}";
        PreviousPage.IsEnabled = _page > 0;
        NextPage.IsEnabled = _page + 1 < document.PageCount;
        ContentScroll.ChangeView(0, 0, null, true);
    }

    private async void OnPreviousPage(object sender, RoutedEventArgs e) => await ChangePageAsync(-1);
    private async void OnNextPage(object sender, RoutedEventArgs e) => await ChangePageAsync(1);

    private async Task ChangePageAsync(int delta)
    {
        if (_read is null || _pdf is null) return;
        long next = (long)_page + delta;
        if (next < 0 || next >= _pdf.PageCount) return;
        PreviousPage.IsEnabled = NextPage.IsEnabled = false;
        _page = (uint)next;
        long generation = ++_generation;
        try { await RenderPdfPageAsync(_read.Token, generation); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (generation != _generation) return;
            StatusLabel.Text = PreviewError(error); StatusLabel.Visibility = Visibility.Visible;
        }
    }
}
