using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class LogView : UserControl, IDisposable
    {
        private ApiService? _apiService;
        private DispatcherTimer? _refreshTimer;
        private List<ActivityLogEventDto> _allEvents = new();

        public ObservableCollection<LogRow> Rows { get; } = new();

        private static readonly TimeZoneInfo VietnamZone =
            TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

        public LogView()
        {
            this.InitializeComponent();
            FilterChat.IsChecked = true;
            FilterEmotion.IsChecked = true;
            FilterVts.IsChecked = true;
        }

        private bool _isActive = true;

        public void Initialize(ApiService apiService)
        {
            _apiService = apiService;
            Rows.Clear();
            Rows.Add(new LogRow("--:--:--", "…", LogRowKind.Info, "Loading logs...", ""));
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _refreshTimer.Tick += async (_, __) => await RefreshAsync();
            if (_isActive)
            {
                _ = RefreshAsync();
                _refreshTimer.Start();
            }
        }

        public void SetActive(bool active)
        {
            _isActive = active;
            if (active)
            {
                _ = RefreshAsync();
                _refreshTimer?.Start();
            }
            else
            {
                _refreshTimer?.Stop();
            }
        }

        private async Task RefreshAsync()
        {
            if (_apiService == null) return;
            try
            {
                var events = await _apiService.GetActivityLogAsync(200);
                if (events == null) return;
                _allEvents = events;
                ApplyFilter();
            }
            catch (Exception)
            {
                // Keep current list if network/API error.
            }
        }

        private string? _lastFilterSignature;
        private bool _isFirstLoad = true;

        private void ApplyFilter(bool isUserAction = false)
        {
            bool showChat = FilterChat.IsChecked == true;
            bool showEmotion = FilterEmotion.IsChecked == true;
            bool showVts = FilterVts.IsChecked == true;

            var rows = _allEvents
                .Where(e => e.Type switch
                {
                    "chat" => showChat,
                    "emotion" => showEmotion,
                    "vts_action" => showVts,
                    _ => true,
                })
                .Select(ToRow)
                .ToList();

            string signature = $"{showChat}_{showEmotion}_{showVts}_{rows.Count}_{(rows.Count > 0 ? rows[^1].Time + rows[^1].Content : string.Empty)}";
            if (!isUserAction && !_isFirstLoad && string.Equals(signature, _lastFilterSignature, StringComparison.Ordinal))
            {
                return;
            }
            _lastFilterSignature = signature;

            Rows.Clear();
            if (rows.Count == 0)
            {
                Rows.Add(new LogRow(
                    TimeVietnam(DateTimeOffset.UtcNow),
                    "…",
                    LogRowKind.Info,
                    "No activity yet.",
                    ""));
            }
            else
            {
                foreach (var row in rows)
                {
                    Rows.Add(row);
                }
                if (isUserAction || _isFirstLoad)
                {
                    TimelineList.ScrollIntoView(rows[rows.Count - 1]);
                }
            }

            _isFirstLoad = false;
        }

        private static LogRow ToRow(ActivityLogEventDto e)
        {
            string time;
            try
            {
                time = TimeVietnam(DateTimeOffset.FromUnixTimeSeconds((long)e.Ts));
            }
            catch
            {
                time = "--:--:--";
            }

            string summary = e.Type switch
            {
                "chat" => FormatChat(e.Data),
                "emotion" => FormatEmotion(e.Data),
                "vts_action" => FormatVts(e.Data),
                "observation_reaction" => FormatObservationReaction(e.Data),
                _ => e.Type ?? "",
            };

            string detail =
                $"Type: {e.Type ?? "-"}\n" +
                $"Time (UTC): {e.Iso ?? "-"}\n" +
                $"Session: {e.SessionId ?? "-"}\n" +
                $"Content: {summary}";

            return e.Type switch
            {
                "chat" => new LogRow(time, GetActivityBadge(e.Type), LogRowKind.Chat, summary, detail),
                "emotion" => new LogRow(time, GetActivityBadge(e.Type), LogRowKind.Emotion, summary, detail),
                "vts_action" => new LogRow(time, GetActivityBadge(e.Type), LogRowKind.Vts, summary, detail),
                "observation_reaction" => new LogRow(time, GetActivityBadge(e.Type), LogRowKind.Observation, summary, detail),
                _ => new LogRow(time, GetActivityBadge(e.Type), LogRowKind.Info, summary, detail),
            };
        }

        private static string GetActivityBadge(string? type)
        {
            return type switch
            {
                "chat" => "CHAT",
                "emotion" => "EMOTION",
                "vts_action" => "VTS",
                "observation_reaction" => "OBSERVATION",
                _ => type ?? "?",
            };
        }

        private static string TimeVietnam(DateTimeOffset utc)
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, VietnamZone);
            return local.ToString("HH:mm:ss");
        }

        private static string FormatChat(JsonElement data)
        {
            try
            {
                string role = data.TryGetProperty("role", out var r) ? (r.GetString() ?? "") : "";
                string preview = data.TryGetProperty("preview", out var p) ? (p.GetString() ?? "") : "";
                if (string.IsNullOrEmpty(preview) && data.TryGetProperty("content", out var c))
                {
                    preview = c.GetString() ?? "";
                }
                return role == "user" ? $"You: {preview}" : $"Ru: {preview}";
            }
            catch
            {
                return "";
            }
        }

        private static string FormatEmotion(JsonElement data)
        {
            try
            {
                string emotion = data.TryGetProperty("emotion", out var em) ? (em.GetString() ?? "unknown") : "unknown";
                return $"Emotion: {emotion}";
            }
            catch
            {
                return "Emotion: unknown";
            }
        }

        private static string FormatVts(JsonElement data)
        {
            try
            {
                string category = data.TryGetProperty("category", out var cat) ? (cat.GetString() ?? "") : "";
                string status = data.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";

                if (!string.IsNullOrEmpty(category))
                    return $"VTS: {category} ({status})";
                return "VTS Action";
            }
            catch
            {
                return "VTS Action";
            }
        }

        private static string FormatObservationReaction(JsonElement data)
        {
            try
            {
                string preview = data.TryGetProperty("preview", out var previewValue)
                    ? previewValue.GetString() ?? ""
                    : "";
                if (!string.IsNullOrWhiteSpace(preview))
                {
                    return $"Observation: {preview}";
                }

                string scene = data.TryGetProperty("scene", out var sceneValue)
                    ? sceneValue.GetString() ?? ""
                    : "";
                string observedEvent = data.TryGetProperty("event", out var eventValue)
                    ? eventValue.GetString() ?? ""
                    : "";
                if (!string.IsNullOrWhiteSpace(scene) && !string.IsNullOrWhiteSpace(observedEvent))
                {
                    return $"Observation: {scene} ({observedEvent})";
                }
                return "Observation Reaction";
            }
            catch
            {
                return "Observation Reaction";
            }
        }

        private void OnFilterToggled(object sender, RoutedEventArgs e)
        {
            ApplyFilter(isUserAction: true);
        }

        private void OnRowClicked(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is not LogRow row) return;

            int index = Rows.IndexOf(row);
            if (index < 0) return;

            row.IsExpanded = !row.IsExpanded;
            Rows.RemoveAt(index);
            Rows.Insert(index, row);
        }

        private async void OnClearClicked(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;

            var dialog = new ContentDialog
            {
                Title = "Clear Logs",
                Content = "Clear all current activity logs?",
                PrimaryButtonText = "Clear",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = this.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            bool ok = await _apiService.ClearActivityLogAsync();
            if (ok)
            {
                _allEvents = new List<ActivityLogEventDto>();
                ApplyFilter(isUserAction: true);
            }
        }

        public void Dispose()
        {
            _isActive = false;
            _refreshTimer?.Stop();
            _refreshTimer = null;
            _apiService = null;
            _allEvents.Clear();
            Rows.Clear();
        }
    }

    public enum LogRowKind
    {
        Chat,
        Emotion,
        Vts,
        Observation,
        Info,
    }

    public sealed class LogRow
    {
        private static readonly SolidColorBrush ChatBrush = FromArgb(0xFF, 0x3C, 0x87, 0xFF);
        private static readonly SolidColorBrush EmotionBrush = FromArgb(0xFF, 0xB0, 0x6E, 0xF0);
        private static readonly SolidColorBrush VtsBrush = FromArgb(0xFF, 0xFF, 0x5C, 0x92);
        private static readonly SolidColorBrush ObservationBrush = FromArgb(0xFF, 0x2D, 0xC9, 0xB8);
        private static readonly SolidColorBrush InfoBrush = FromArgb(0xFF, 0x8A, 0x8A, 0x8A);

        public string Time { get; }
        public string Badge { get; }
        public string Content { get; }
        public string Detail { get; }
        public SolidColorBrush DotBrush { get; }
        public SolidColorBrush LabelBrush { get; }
        public bool IsExpanded { get; set; }

        public LogRow(string time, string badge, LogRowKind kind, string content, string detail)
        {
            Time = time;
            Badge = badge;
            Content = content;
            Detail = detail;
            DotBrush = kind switch
            {
                LogRowKind.Chat => ChatBrush,
                LogRowKind.Emotion => EmotionBrush,
                LogRowKind.Vts => VtsBrush,
                LogRowKind.Observation => ObservationBrush,
                _ => InfoBrush,
            };
            LabelBrush = DotBrush;
        }

        private static SolidColorBrush FromArgb(byte a, byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush();
            brush.Color = Windows.UI.Color.FromArgb(a, r, g, b);
            return brush;
        }

    }
}
