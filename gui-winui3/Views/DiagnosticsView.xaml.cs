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
using Microsoft.UI.Dispatching;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class DiagnosticsView : UserControl, IDisposable
    {
        private ApiService? _apiService;
        private DispatcherTimer? _refreshTimer;
        private DispatcherQueueTimer? _filterDebounceTimer;
        private RefreshController<(DiagnosticStatusDto? Status, DiagnosticLogResultDto? Logs)>? _refreshController;
        private readonly List<DiagnosticEventDto> _allEvents = new();
        private readonly HashSet<string> _seenEventIds = new(StringComparer.Ordinal);
        private readonly Dictionary<DiagnosticEventDto, DiagnosticRow> _rowCache = new();
        private IReadOnlyList<DiagnosticEventDto>? _lastVisibleEvents;
        private string? _cursor;
        private bool _isPaused;
        private bool _isActive;
        private ResponsiveLayoutTier? _statusMetricsTier;

        // One shared instance: ToRow allocated a new options object per event,
        // per keystroke, for nothing but WriteIndented.
        private static readonly JsonSerializerOptions DiagnosticDetailOptions = new() { WriteIndented = true };

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
            // A reconnect calls this again with a new service and a new controller. Creating
            // them without tearing the old pair down left a live 3s timer and a polling
            // controller behind on every reconnect, all of them pointing at the disposed
            // service that DisposeAsync had already replaced.
            DisposeRefreshInfrastructure();

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
            try
            {
                if (_refreshController != null) await _refreshController.RefreshAsync();
                else await RefreshAsync();
            }
            catch (Exception ex)
            {
                // async void on a timer: a failed refresh callback must not take
                // the dispatcher down; the next tick tries again.
                GuiDiagnosticWriter.AppendTraceLine($"[DiagnosticsView] Refresh failed: {ex.Message}");
            }
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
                        _rowCache.Remove(_allEvents[i]);
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
                GuiDiagnosticWriter.AppendTraceLine($"[DiagnosticsView] Refresh failed: {ex.GetType().Name}: {ex.Message}");
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
            // Events are stable instances: only rows for newly seen events pay
            // for serialization, the rest are reused across keystrokes.
            var rows = visibleEvents.Select(GetCachedRow).ToList();
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

        private DiagnosticRow GetCachedRow(DiagnosticEventDto e)
        {
            if (_rowCache.TryGetValue(e, out var row)) return row;
            row = ToRow(e);
            _rowCache[e] = row;
            return row;
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
                detail = JsonSerializer.Serialize(e, DiagnosticDetailOptions);
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
            ScheduleFilter();
        }

        private void OnEventCodeFilterChanged(object sender, TextChangedEventArgs e)
        {
            ScheduleFilter();
        }

        private void OnLevelSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ScheduleFilter();
        }

        private void OnCorrelationFilterChanged(object sender, TextChangedEventArgs e)
        {
            ScheduleFilter();
        }

        /// <summary>
        /// Coalesces a burst of filter keystrokes into one trailing apply: every
        /// keystroke re-scanned the events and re-serialized each visible row.
        /// </summary>
        private void ScheduleFilter()
        {
            if (_filterDebounceTimer is null)
            {
                _filterDebounceTimer = DispatcherQueue.CreateTimer();
                _filterDebounceTimer.Interval = UiTiming.DiagnosticFilterDebounce;
                _filterDebounceTimer.Tick += OnFilterDebounceTick;
            }

            _filterDebounceTimer.Stop();
            _filterDebounceTimer.Start();
        }

        private void OnFilterDebounceTick(object? sender, object e)
        {
            _filterDebounceTimer?.Stop();
            ApplyFilter();
        }

        private async void OnExportClicked(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;

            try
            {
                ExportButton.IsEnabled = false;
                ExportButton.Content = "Exporting…";

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
                GuiDiagnosticWriter.AppendTraceLine($"[DiagnosticsView] Export failed: {ex.GetType().Name}: {ex.Message}");
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
            DisposeRefreshInfrastructure();
            _isActive = false;
            _apiService = null;
        }

        /// <summary>
        /// Tears down the timer and controller pair. Both <see cref="Initialize"/> and
        /// <see cref="Dispose"/> need this, and doing it in one place is what keeps a
        /// re-Initialize from stacking a second pair on top of the first.
        /// </summary>
        private void DisposeRefreshInfrastructure()
        {
            if (_refreshTimer != null)
            {
                _refreshTimer.Tick -= OnRefreshTimerTick;
                _refreshTimer.Stop();
                _refreshTimer = null;
            }
            if (_filterDebounceTimer != null)
            {
                _filterDebounceTimer.Tick -= OnFilterDebounceTick;
                _filterDebounceTimer.Stop();
                _filterDebounceTimer = null;
            }

            _refreshController?.SetActive(false);
            if (_refreshController != null)
            {
                var controller = _refreshController;
                _refreshController = null;
                // Fire-and-forget with ConfigureAwait(false) inside: blocking
                // here froze the UI thread whenever the in-flight refresh
                // arrived after this call began.
                _ = controller.DisposeAsync().AsTask().ContinueWith(
                    static faulted => GuiDiagnosticWriter.AppendTraceLine(
                        $"[DiagnosticsView] Refresh dispose failed: {faulted!.Exception!.GetBaseException().Message}"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
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
            "ERROR" or "CRITICAL" => (SolidColorBrush)Application.Current.Resources["RuDangerBrush"],
            "WARNING" or "WARN" => (SolidColorBrush)Application.Current.Resources["RuWarningBrush"],
            _ => (SolidColorBrush)Application.Current.Resources["RuInfoBrush"],
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
