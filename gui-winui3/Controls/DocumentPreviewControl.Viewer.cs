using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace RuApp.Controls;

/// <summary>
/// The viewer chrome around the rendered page: zoom for raster surfaces and page
/// navigation for a paged document. Both act on <c>ContentScroll</c>, so a zoom keeps
/// its place instead of re-rendering the file.
/// </summary>
public sealed partial class DocumentPreviewControl
{
    private const float ZoomStep = 1.25f;
    private bool _syncingGutter;

    private void OnPreviewViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => RefreshZoom();

    /// <summary>
    /// Keeps the numbers on their own lines. The code column gives up the height of its
    /// horizontal bar, so the gutter's viewport is trimmed by that same amount before the
    /// two vertical offsets are matched; otherwise the last numbers would drift.
    /// </summary>
    private void SyncCodeGutter()
    {
        if (CodeFrame.Visibility != Visibility.Visible || _syncingGutter) return;
        _syncingGutter = true;
        try
        {
            double trim = Math.Max(0, CodeScroll.ActualHeight - CodeScroll.ViewportHeight);
            if (Math.Abs(GutterScroll.Margin.Bottom - trim) > 0.5)
            {
                GutterScroll.Margin = new Thickness(0, 0, 0, trim);
            }

            if (Math.Abs(GutterScroll.VerticalOffset - CodeScroll.VerticalOffset) > 0.5)
            {
                GutterScroll.ChangeView(null, CodeScroll.VerticalOffset, null, true);
            }
        }
        finally { _syncingGutter = false; }
    }

    private void OnCodeScrollChanged(object? sender, ScrollViewerViewChangedEventArgs e) => SyncCodeGutter();

    private void OnCodeScrollSizeChanged(object? sender, SizeChangedEventArgs e) => SyncCodeGutter();

    /// <summary>
    /// A wheel over the numbers scrolls the gutter, which has no bar of its own; hand that
    /// offset to the code so the two columns move together whichever one the reader scrolled.
    /// </summary>
    private void OnGutterScrollChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_syncingGutter) return;
        if (Math.Abs(CodeScroll.VerticalOffset - GutterScroll.VerticalOffset) > 0.5)
        {
            CodeScroll.ChangeView(null, GutterScroll.VerticalOffset, null, true);
        }
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(ContentScroll.ZoomFactor * ZoomStep);
    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(ContentScroll.ZoomFactor / ZoomStep);

    private void SetZoom(float factor)
    {
        float zoom = Math.Clamp(factor, ContentScroll.MinZoomFactor, ContentScroll.MaxZoomFactor);
        if (Math.Abs(zoom - ContentScroll.ZoomFactor) < 0.001f) return;
        ContentScroll.ChangeView(null, null, zoom, true);
    }

    /// <summary>Shows the current zoom and stops the buttons at the ends of the range.</summary>
    private void RefreshZoom()
    {
        ZoomLabel.Text = Math.Round(ContentScroll.ZoomFactor * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        ZoomOutButton.IsEnabled = ContentScroll.ZoomFactor > ContentScroll.MinZoomFactor + 0.001f;
        ZoomInButton.IsEnabled = ContentScroll.ZoomFactor < ContentScroll.MaxZoomFactor - 0.001f;
    }

    /// <summary>Returns the label to its resting value while no file is shown.</summary>
    private void ResetZoom()
    {
        ZoomLabel.Text = "100%";
        ZoomOutButton.IsEnabled = true;
        ZoomInButton.IsEnabled = true;
    }

    private async void OnPreviousPage(object sender, RoutedEventArgs e) => await ChangePageAsync(-1);
    private async void OnNextPage(object sender, RoutedEventArgs e) => await ChangePageAsync(1);

    private async void OnPageInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await GoToRequestedPageAsync();
    }

    private async void OnPageInputLostFocus(object sender, RoutedEventArgs e) => await GoToRequestedPageAsync();

    /// <summary>
    /// Reads the page box. Anything that is not a page number snaps back to the page
    /// being shown, so the box never disagrees with the rendered page.
    /// </summary>
    private async Task GoToRequestedPageAsync()
    {
        if (_read is null || _pdf is not { } document) return;
        if (!int.TryParse(PageInput.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int requested))
        {
            SyncPageInput();
            return;
        }

        int target = Math.Clamp(requested, 1, (int)document.PageCount) - 1;
        if (target == _page) SyncPageInput();
        else await GoToPageAsync(target);
    }

    private async Task ChangePageAsync(int delta)
    {
        if (_pdf is not { } document) return;
        long next = (long)_page + delta;
        if (next < 0 || next >= document.PageCount) return;
        await GoToPageAsync((int)next);
    }

    private async Task GoToPageAsync(int page)
    {
        if (_read is null || _pdf is not { } document || page < 0 || page >= document.PageCount) return;
        PreviousPage.IsEnabled = NextPage.IsEnabled = PageInput.IsEnabled = false;
        _page = (uint)page;
        long generation = ++_generation;
        try { await RenderPdfPageAsync(_read.Token, generation); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (generation != _generation) return;
            SyncPageInput();
            RefreshPageControls();
            StatusLabel.Text = PreviewError(error); StatusLabel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// The pager's state for the page being shown. A failed render leaves it here rather
    /// than disabled, so the reader can try another page instead of reloading the file.
    /// </summary>
    private void RefreshPageControls()
    {
        if (_pdf is not { } document) return;
        PageInput.IsEnabled = true;
        PreviousPage.IsEnabled = _page > 0;
        NextPage.IsEnabled = _page + 1 < document.PageCount;
    }

    /// <summary>Shows the page the viewer is actually on: after a clamp, a page change or a collision.</summary>
    private void SyncPageInput() => PageInput.Text = (_page + 1).ToString(CultureInfo.InvariantCulture);
}
