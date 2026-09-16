using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class DiagnosticsView : UserControl, IDisposable
    {
        private ApiService? _apiService;
        private DispatcherTimer? _refreshTimer;
        private RefreshController<(DiagnosticStatusDto? Status, DiagnosticLogResultDto? Logs)>? _refreshController;
        private readonly List<DiagnosticEventDto> _allEvents = new();
        private readonly HashSet<string> _seenEventIds = new(StringComparer.Ordinal);
        private IReadOnlyList<DiagnosticEventDto>? _lastVisibleEvents;
        private string? _cursor;
        private bool _isPaused;
        private bool _isActive;
        private ResponsiveLayoutTier? _statusMetricsTier;

        public BatchObservableCollection<DiagnosticRow> Rows { get; } = new();

        // The level picker's entries come from the same module that maps them back to a
        // filter, so what the user picks and what is filtered on cannot drift.
        public IReadOnlyList<string> LevelFilterLabels => DiagnosticSeverityFilter.PickerLabels;

        private static readonly TimeZoneInfo VietnamZone =
            TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

        public DiagnosticsView()
        {
            this.InitializeComponent();
        }

        private void ResponsiveRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0)
            {
                return;
            }

            var tier = ResponsiveLayoutPolicy.GetForWidth(e.NewSize.Width).Tier;
            if (_statusMetricsTier == tier)
            {
                return;
            }

            _statusMetricsTier = tier;
            ReflowStatusMetrics(tier);
        }

        private void ReflowStatusMetrics(ResponsiveLayoutTier tier)
        {
            var panels = new[]
            {
                QueueMetricPanel,
                DroppedMetricPanel,
                FailuresMetricPanel,
                FilesMetricPanel
            };

            var columns = tier == ResponsiveLayoutTier.Wide ? 4
                : tier == ResponsiveLayoutTier.Compact ? 2
                : 1;
            var rows = (panels.Length + columns - 1) / columns;

            StatusMetricsGrid.ColumnDefinitions.Clear();
            for (var index = 0; index < columns; index++)
            {
                StatusMetricsGrid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
            }

            StatusMetricsGrid.RowDefinitions.Clear();
            for (var index = 0; index < rows; index++)
            {
                StatusMetricsGrid.RowDefinitions.Add(new RowDefinition
                {
                    Height = GridLength.Auto
                });
            }

            for (var index = 0; index < panels.Length; index++)
            {
                Grid.SetColumn(panels[index], index % columns);
                Grid.SetRow(panels[index], index / columns);
            }
        }

        public void Initialize(ApiService apiService)
        {
            _apiService = apiService;
            Rows.ReplaceAll(new[]
            {
                new DiagnosticRow("--:--:--", "INFO", "host", "Dang tai du lieu chan doan...", "")
            });
            _refreshController = new RefreshController<(DiagnosticStatusDto? Status, DiagnosticLogResultDto? Logs)>(
                async token =>
                {
                    var status = await _apiService.GetDiagnosticStatusAsync(token);
                    DiagnosticLogResultDto? logs = null;
                    if (!_isPaused)
                    {
                        logs = await _apiService.GetDiagnosticLogsAsync(limit: 100, cursor: _cursor, cancellationToken: token);
                    }
                    return (status, logs);
                },
                result => ApplyRefreshResult(result.Status, result.Logs),
                action =>
                {
                    if (!DispatcherQueue.TryEnqueue(() => action()))
                    {
                        throw new OperationCanceledException("The diagnostics dispatcher rejected a refresh callback.");
                    }
                });
            _refreshController.SetActive(_isActive);
            _ = _refreshController.RefreshAsync();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _refreshTimer.Tick += OnRefreshTimerTick;
            _refreshTimer.Start();
        }

        private async void OnRefreshTimerTick(object? sender, object e)
        {
            if (_refreshController != null) await _refreshController.RefreshAsync();
            else await RefreshAsync();
        }

        public void SetActive(bool active)
        {
            _isActive = active;
            _refreshController?.SetActive(active);
            if (active)
            {
                _ = _refreshController?.RefreshAsync();
                _refreshTimer?.Start();
            }
            else
            {
                _refreshTimer?.Stop();
            }
        }

        private void ApplyRefreshResult(DiagnosticStatusDto? status, DiagnosticLogResultDto? result)
        {
            if (status != null)
            {
                QueueDepthText.Text = $"Depth: {status.Writer?.QueueDepth ?? 0}";
                DroppedCountText.Text = $"{status.Writer?.DroppedCount ?? 0} entries";
                WriteFailuresText.Text = $"{status.Writer?.WriteFailureCount ?? 0} errors";
                double kb = (status.TotalSizeBytes) / 1024.0;
                TotalFilesText.Text = $"{status.TotalLogFiles} files ({kb:F1} KB)";
                if (status.Writer != null && (status.Writer.DroppedCount > 0 || status.Writer.WriteFailureCount > 0))
                {
                    WarningBanner.Visibility = Visibility.Visible;
                    WarningBannerText.Text = $"Warning: Detected {status.Writer.WriteFailureCount} write errors and {status.Writer.DroppedCount} dropped log entries!";
                }
                else
                {
                    WarningBanner.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                WarningBanner.Visibility = Visibility.Visible;
                WarningBannerText.Text = "Could not read backend diagnostic writer status.";
            }

            if (_isPaused) return;
            if (result == null)
            {
                if (status == null)
                {
                    WarningBanner.Visibility = Visibility.Visible;
                    WarningBannerText.Text = "Could not read backend diagnostic logs.";
                }
                return;
            }
            if (result.Events.Count > 0)
            {
                foreach (var evt in result.Events)
                {
                    if (string.IsNullOrEmpty(evt.EventId) || _seenEventIds.Add(evt.EventId))
                    {
                        _allEvents.Add(evt);
                    }
                }
                if (_allEvents.Count > 1000)
                {
                    var removeCount = _allEvents.Count - 1000;
                    for (int i = 0; i < removeCount; i++)
                    {
                        if (!string.IsNullOrEmpty(_allEvents[i].EventId))
                        {
                            _seenEventIds.Remove(_allEvents[i].EventId);
                        }
                    }
                    _allEvents.RemoveRange(0, removeCount);
                }
                _cursor = result.Cursor ?? _cursor;
                ApplyFilter();
            }
            else if (string.IsNullOrEmpty(_cursor) && _allEvents.Count == 0)
            {
                ApplyFilter();
            }
        }

        public void FilterByCorrelation(string correlationId)
        {
            CorrelationFilterBox.Text = correlationId ?? "";
            ApplyFilter();
        }

        private async Task RefreshAsync()
        {
            if (_apiService == null) return;
            try
            {
                // 1. Update health status counters
                var status = await _apiService.GetDiagnosticStatusAsync();
                if (status != null)
                {
                    QueueDepthText.Text = $"Depth: {status.Writer?.QueueDepth ?? 0}";
                    DroppedCountText.Text = $"{status.Writer?.DroppedCount ?? 0} entries";
                    WriteFailuresText.Text = $"{status.Writer?.WriteFailureCount ?? 0} errors";

                    double kb = (status.TotalSizeBytes) / 1024.0;
                    TotalFilesText.Text = $"{status.TotalLogFiles} files ({kb:F1} KB)";

                    if (status.Writer != null && (status.Writer.DroppedCount > 0 || status.Writer.WriteFailureCount > 0))
                    {
                        WarningBanner.Visibility = Visibility.Visible;
                        WarningBannerText.Text = $"Warning: Detected {status.Writer.WriteFailureCount} write errors and {status.Writer.DroppedCount} dropped log entries!";
                    }
                    else
                    {
                        WarningBanner.Visibility = Visibility.Collapsed;
                    }
                }
                else
                {
                    WarningBanner.Visibility = Visibility.Visible;
                    WarningBannerText.Text = "Could not read backend diagnostic writer status.";
                }

                // 2. Incremental log fetch
                if (_isPaused) return;

                var result = await _apiService.GetDiagnosticLogsAsync(limit: 100, cursor: _cursor);
                if (result == null)
                {
                    WarningBanner.Visibility = Visibility.Visible;
                    WarningBannerText.Text = "Could not read backend diagnostic logs.";
                    return;
                }

                if (result.Events.Count > 0)
                {
                    foreach (var evt in result.Events)
                    {
                        if (string.IsNullOrEmpty(evt.EventId) || _seenEventIds.Add(evt.EventId))
                        {
                            _allEvents.Add(evt);
                        }
                    }

                    if (_allEvents.Count > 1000)
                    {
                        var removeCount = _allEvents.Count - 1000;
                        for (int i = 0; i < removeCount; i++)
                        {
                            if (!string.IsNullOrEmpty(_allEvents[i].EventId))
                            {
                                _seenEventIds.Remove(_allEvents[i].EventId);
                            }
                        }
                        _allEvents.RemoveRange(0, removeCount);
                    }

                    _cursor = result.Cursor ?? _cursor;
                    ApplyFilter();
                }
                else if (string.IsNullOrEmpty(_cursor) && _allEvents.Count == 0)
                {
                    ApplyFilter();
                }
            }
            catch (Exception ex)
            {
                WarningBanner.Visibility = Visibility.Visible;
                WarningBannerText.Text = "Could not connect to backend diagnostics service.";
                System.Diagnostics.Debug.WriteLine($"[DiagnosticsView] Refresh failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnPauseClicked(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused;
            PauseButton.Content = _isPaused ? "Resume" : "Pause";
            PauseButton.Foreground = _isPaused
                ? (SolidColorBrush)Application.Current.Resources["AppAccentHoverBrush"]
                : (SolidColorBrush)Application.Current.Resources["AppTextPrimaryBrush"];
        }

        private void ApplyFilter()
        {
            string? selectedLevel = DiagnosticSeverityFilter.ForSelectedIndex(LevelFilterCombo.SelectedIndex);

            string compFilter = ComponentFilterBox.Text?.Trim() ?? "";
            string eventCodeFilter = EventCodeFilterBox.Text?.Trim() ?? "";
            string corrFilter = CorrelationFilterBox.Text?.Trim() ?? "";

            var visibleEvents = _allEvents
                .Where(e =>
                {
                    if (selectedLevel != null && !string.Equals(e.Level, selectedLevel, StringComparison.OrdinalIgnoreCase))
                        return false;
                    if (!string.IsNullOrEmpty(compFilter) && !(e.Component?.Contains(compFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                        return false;
                    if (!string.IsNullOrEmpty(eventCodeFilter) && !(e.EventCode?.Contains(eventCodeFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                        return false;
                    if (!string.IsNullOrEmpty(corrFilter) && !(e.CorrelationId?.Contains(corrFilter, StringComparison.OrdinalIgnoreCase) ?? false))
                        return false;
                    return true;
                })
                .ToList();

            if (_lastVisibleEvents is not null && _lastVisibleEvents.SequenceEqual(visibleEvents))
            {
                return;
            }

            _lastVisibleEvents = visibleEvents;
            var rows = visibleEvents.Select(ToRow).ToList();
            if (rows.Count == 0)
            {
                rows.Add(new DiagnosticRow(
                    TimeVietnam(DateTimeOffset.UtcNow),
                    "INFO",
                    "host",
                    "No matching diagnostic events found.",
                    ""));
            }

            ReplaceRows(rows);
        }

        private void ReplaceRows(IReadOnlyList<DiagnosticRow> rows)
        {
            var scrollViewer = FindScrollViewer(DiagnosticsList);
            var shouldRestoreScroll = scrollViewer is not null && Rows.Count > 1;
            var previousOffset = scrollViewer?.VerticalOffset ?? 0;
            var wasAtBottom = scrollViewer is not null &&
                              scrollViewer.ScrollableHeight > 0 &&
                              scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 1;

            Rows.ReplaceAll(rows);

            if (!shouldRestoreScroll || scrollViewer is null)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                var targetOffset = wasAtBottom
                    ? scrollViewer.ScrollableHeight
                    : Math.Min(previousOffset, scrollViewer.ScrollableHeight);
                scrollViewer.ChangeView(null, targetOffset, null, disableAnimation: true);
            });
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject? root)
        {
            if (root is null)
            {
                return null;
            }

            if (root is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var result = FindScrollViewer(VisualTreeHelper.GetChild(root, index));
                if (result is not null)
                {
                    return result;
                }
            }

            return null;
        }

        private static DiagnosticRow ToRow(DiagnosticEventDto e)
        {
            string time;
            try
            {
                var dto = DateTimeOffset.FromUnixTimeMilliseconds((long)(e.Ts * 1000));
                time = TimeVietnam(dto);
            }
            catch
            {
                time = "--:--:--";
            }

            var level = string.IsNullOrWhiteSpace(e.Level) ? "INFO" : e.Level.ToUpperInvariant();
            var component = string.IsNullOrWhiteSpace(e.Component) ? "host" : e.Component;

            var summary = $"{e.EventCode}";
            if (!string.IsNullOrWhiteSpace(e.Status)) summary += $" [{e.Status}]";
            if (e.DurationMs.HasValue) summary += $" ({e.DurationMs.Value:0.##}ms)";
            if (!string.IsNullOrWhiteSpace(e.ErrorCode)) summary += $" error={e.ErrorCode}";
            if (!string.IsNullOrWhiteSpace(e.CorrelationId)) summary += $" (corr: {e.CorrelationId})";

            string detail;
            try
            {
                detail = JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true });
            }
            catch
            {
                detail = e.ToString() ?? "";
            }

            return new DiagnosticRow(time, level, component, summary, detail);
        }

        private static string TimeVietnam(DateTimeOffset utc)
        {
            try
            {
                return TimeZoneInfo.ConvertTime(utc, VietnamZone).ToString("HH:mm:ss");
            }
            catch
            {
                return utc.ToString("HH:mm:ss");
            }
        }

        private void OnComponentFilterChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void OnEventCodeFilterChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void OnLevelSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilter();
        }

        private void OnCorrelationFilterChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        private async void OnExportClicked(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;

            try
            {
                ExportButton.IsEnabled = false;
                ExportButton.Content = "Dang xuat...";

                string? selectedLevel = DiagnosticSeverityFilter.ForSelectedIndex(LevelFilterCombo.SelectedIndex);
                string? corr = string.IsNullOrWhiteSpace(CorrelationFilterBox.Text) ? null : CorrelationFilterBox.Text.Trim();
                string? component = string.IsNullOrWhiteSpace(ComponentFilterBox.Text) ? null : ComponentFilterBox.Text.Trim();
                string? eventCode = string.IsNullOrWhiteSpace(EventCodeFilterBox.Text) ? null : EventCodeFilterBox.Text.Trim();

                var ndjson = await _apiService.ExportDiagnosticLogsAsync(
                    level: selectedLevel,
                    component: component,
                    eventCode: eventCode,
                    correlationId: corr);
                if (ndjson == null)
                {
                    WarningBanner.Visibility = Visibility.Visible;
                    WarningBannerText.Text = "Could not export backend diagnostic logs.";
                    return;
                }

                var savePicker = new Windows.Storage.Pickers.FileSavePicker();
                savePicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                savePicker.FileTypeChoices.Add("NDJSON file", new List<string> { ".ndjson", ".jsonl" });
                savePicker.SuggestedFileName = $"diagnostics-export-{DateTime.UtcNow:yyyyMMdd-HHmmss}.ndjson";

                if (MainWindow.Instance != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
                    WinRT.Interop.InitializeWithWindow.Initialize(savePicker, hwnd);
                }

                var file = await savePicker.PickSaveFileAsync();
                if (file != null)
                {
                    await Windows.Storage.FileIO.WriteTextAsync(file, ndjson);
                }
            }
            catch (Exception ex)
            {
                WarningBanner.Visibility = Visibility.Visible;
                WarningBannerText.Text = "Exporting diagnostic logs failed.";
                System.Diagnostics.Debug.WriteLine($"[DiagnosticsView] Export failed: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                ExportButton.IsEnabled = true;
                ExportButton.Content = "Export NDJSON";
            }
        }

        private void OnRowClicked(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is DiagnosticRow row)
            {
                row.IsExpanded = !row.IsExpanded;
            }
        }

        public void Dispose()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Tick -= OnRefreshTimerTick;
                _refreshTimer.Stop();
                _refreshTimer = null;
            }
            _isActive = false;
            _refreshController?.SetActive(false);
            if (_refreshController != null)
            {
                _ = _refreshController.DisposeAsync().AsTask();
                _refreshController = null;
            }
        }
    }

    public sealed class DiagnosticRow : INotifyPropertyChanged
    {
        public string Time { get; }
        public string Level { get; }
        public string Component { get; }
        public string Summary { get; }
        public string Detail { get; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged();
                }
            }
        }

        public SolidColorBrush LevelBrush => Level.ToUpperInvariant() switch
        {
            "ERROR" or "CRITICAL" => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 239, 68, 68)),
            "WARNING" or "WARN" => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 245, 158, 11)),
            _ => new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 59, 130, 246)),
        };

        public SolidColorBrush DotBrush => LevelBrush;

        public DiagnosticRow(string time, string level, string component, string summary, string detail)
        {
            Time = time;
            Level = level;
            Component = component;
            Summary = summary;
            Detail = detail;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
