using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Services;
using Windows.Data.Pdf;

namespace RuApp.Controls;

/// <summary>Owns one current file, cancels superseded reads and hides native children outside its surface.</summary>
public sealed partial class DocumentPreviewControl : UserControl, IDisposable
{
    private CancellationTokenSource? _read;
    private long _generation;
    private string? _workspace, _path;
    private bool _active, _disposed, _nativeReady;
    private bool _officeUnavailable;
    private PreviewStaWorker? _worker;
    private ShellDocumentPreview? _office;
    private PdfDocument? _pdf;
    private uint _page;
    private Windows.Storage.Streams.IRandomAccessStream? _pdfStream;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _nativeMonitor;
    public Func<bool>? IsOccluded { get; set; }

    public DocumentPreviewControl()
    {
        InitializeComponent();
        // Popup closure and foreground changes do not always invalidate XAML layout.
        // This observer runs only while a native document surface is selected.
        _nativeMonitor = DispatcherQueue.CreateTimer();
        _nativeMonitor.Interval = TimeSpan.FromMilliseconds(100);
        _nativeMonitor.Tick += OnNativeMonitorTick;
        LayoutUpdated += OnPreviewLayoutUpdated;
        Unloaded += OnPreviewUnloaded;
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (active && _office is not null) _nativeMonitor.Start(); else _nativeMonitor.Stop();
        UpdateNativeBounds();
    }

    public void Clear()
    {
        _generation++;
        _nativeMonitor.Stop();
        _read?.Cancel(); _read?.Dispose(); _read = null;
        _office?.Dispose(); _office = null; _nativeReady = false;
        _pdf = null; _pdfStream?.Dispose(); _pdfStream = null;
        _path = _workspace = null;
        PageImage.Source = null; TextContent.Text = ""; MarkdownContent.MarkdownText = "";
        PageToolbar.Visibility = ContentScroll.Visibility = Visibility.Collapsed;
        FocusPreviewButton.Visibility = Visibility.Collapsed;
        FitPreviewButton.Visibility = Visibility.Collapsed;
        StatusLabel.Text = "Select a file in Files to preview it here.";
        StatusLabel.Visibility = Visibility.Visible;
        FileNameLabel.Text = "File preview";
    }

    public async Task OpenAsync(string workspace, string path)
    {
        if (_disposed) return;
        Clear();
        _workspace = workspace; _path = path;
        FileNameLabel.Text = System.IO.Path.GetFileName(path);
        ToolTipService.SetToolTip(FileNameLabel, path);
        StatusLabel.Text = "Rendering preview…";
        _read = new(); var ct = _read.Token; long generation = _generation;
        try
        {
            var kind = FilePreviewSource.Classify(path);
            if (kind == FilePreviewKind.Office)
            {
                if (_officeUnavailable) throw new NotSupportedException("preview_renderer_unresponsive: Restart Ru to reconnect to the document renderer.");
                _worker ??= new PreviewStaWorker();
                nint owner = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
                var office = new ShellDocumentPreview(_worker, owner,
                    () => DispatcherQueue.TryEnqueue(() =>
                    {
                        if (_disposed || generation != _generation || !_active || !ShellDocumentPreview.HasForegroundOwner(owner)) return;
                        MainWindow.Instance?.Activate();
                        PreviewReloadButton.Focus(FocusState.Keyboard);
                    }));
                _office = office;
                if (_active) _nativeMonitor.Start();
                var opening = office.OpenAsync(workspace, path, ct);
                UpdateNativeBounds();
                await opening;
                ct.ThrowIfCancellationRequested();
                if (generation != _generation) return;
                _nativeReady = true;
                FocusPreviewButton.Visibility = Visibility.Visible;
                StatusLabel.Visibility = Visibility.Collapsed;
                UpdateNativeBounds();
            }
            else
            {
                // Bounds and canonical handle checks run away from the dispatcher.
                byte[] bytes = await Task.Run(async () =>
                {
                    await using var file = FilePreviewSource.Open(workspace, path);
                    var result = new byte[checked((int)file.Length)];
                    await file.ReadExactlyAsync(result, ct);
                    return result;
                }, ct);
                ct.ThrowIfCancellationRequested();
                if (generation != _generation) return;
                await RenderAsync(kind, bytes, ct, generation);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (generation != _generation || _disposed) return;
            if (error is TimeoutException) _officeUnavailable = true;
            _office?.Dispose(); _office = null;
            _nativeMonitor.Stop(); _nativeReady = false;
            FocusPreviewButton.Visibility = Visibility.Collapsed;
            StatusLabel.Text = PreviewError(error);
            StatusLabel.Visibility = Visibility.Visible;
            GuiDiagnosticWriter.AppendTraceLine($"[Preview] Render failed: {error.GetType().Name}");
        }
    }

    private static string PreviewError(Exception error) => error switch
    {
        TimeoutException => "preview_timeout: The document renderer did not respond. Other file types still work; restart Ru to reconnect to the document renderer.",
        NotSupportedException => error.Message,
        UnauthorizedAccessException => "preview_access_denied: This file cannot be previewed from the current workspace.",
        IOException when error.Message.StartsWith("preview_", StringComparison.Ordinal) => error.Message,
        IOException => "preview_read_failed: The file may be missing or in use. Reload to try again.",
        _ => "preview_render_failed: The file could not be rendered. Check the document and its installed preview handler, then reload.",
    };

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (_workspace is { } workspace && _path is { } path) await OpenAsync(workspace, path);
    }

    private async void OnFocusPreview(object sender, RoutedEventArgs e)
    {
        if (_office is not { } office || _read is not { } read) return;
        try { await office.FocusAsync(read.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!ReferenceEquals(_office, office)) return;
            StatusLabel.Text = PreviewError(error); StatusLabel.Visibility = Visibility.Visible;
        }
    }

    private void OnPreviewLayoutUpdated(object? sender, object e) => UpdateNativeBounds();
    private void OnNativeMonitorTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) => UpdateNativeBounds();
    private void OnPreviewViewportSizeChanged(object sender, SizeChangedEventArgs e) =>
        PreviewContent.Width = Math.Max(1, ContentScroll.ActualWidth - 16);
    private void OnFitPreview(object sender, RoutedEventArgs e)
    {
        ContentScroll.UpdateLayout();
        ContentScroll.ChangeView(0, 0, 1, true);
    }
    private void OnPreviewUnloaded(object sender, RoutedEventArgs e) => UpdateNativeBounds();

    private void UpdateNativeBounds()
    {
        if (_office is null || XamlRoot?.Content is not UIElement root) return;
        bool visible = _active && _nativeReady && IsLoaded && PreviewArea.ActualWidth > 0 && PreviewArea.ActualHeight > 0;
        // HWND content must not paint over XAML flyouts, dialogs or approval/question overlays.
        visible &= !VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot).Any(popup => popup.Child is not ToolTip);
        visible &= IsOccluded?.Invoke() != true;
        for (DependencyObject? ancestor = this; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is FrameworkElement { Visibility: Visibility.Collapsed }) visible = false;
        var point = PreviewArea.TransformToVisual(root).TransformPoint(new(0, 0));
        double scale = XamlRoot.RasterizationScale;
        _office.Position((int)Math.Round(point.X * scale), (int)Math.Round(point.Y * scale),
            (int)Math.Round(PreviewArea.ActualWidth * scale), (int)Math.Round(PreviewArea.ActualHeight * scale), visible);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Clear();
        LayoutUpdated -= OnPreviewLayoutUpdated; Unloaded -= OnPreviewUnloaded;
        _nativeMonitor.Tick -= OnNativeMonitorTick;
        _worker?.Dispose(); _worker = null;
    }
}
