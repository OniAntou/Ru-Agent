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
        }

        public void Initialize(ApiService apiService)
        {
            _apiService = apiService;
            Rows.Clear();
            Rows.Add(new LogRow("--:--:--", "…", LogRowKind.Info, "Đang tải nhật ký...", ""));
            _ = RefreshAsync();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _refreshTimer.Tick += async (_, __) => await RefreshAsync();
            _refreshTimer.Start();
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
                // Giữ nguyên danh sách hiện tại nếu lỗi mạng/API.
            }
        }

        private void ApplyFilter()
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

            Rows.Clear();
            if (rows.Count == 0)
            {
                Rows.Add(new LogRow(
                    TimeVietnam(DateTimeOffset.UtcNow),
                    "…",
                    LogRowKind.Info,
                    "Chưa có hoạt động nào.",
                    ""));
            }
            else
            {
                foreach (var row in rows)
                {
                    Rows.Add(row);
                }
                TimelineList.ScrollIntoView(rows[rows.Count - 1]);
            }
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

            string detail =
                $"Loại: {e.Type ?? "-"}\n" +
                $"Thời gian (UTC): {e.Iso ?? "-"}\n" +
                $"Phiên: {e.SessionId ?? "-"}\n" +
                $"Dữ liệu: {e.Data.ToString()}";

            return e.Type switch
            {
                "chat" => new LogRow(time, "CHAT", LogRowKind.Chat, FormatChat(e.Data), detail),
                "emotion" => new LogRow(time, "CẢM XÚC", LogRowKind.Emotion, FormatEmotion(e.Data), detail),
                "vts_action" => new LogRow(time, "VTS", LogRowKind.Vts, FormatVts(e.Data), detail),
                _ => new LogRow(time, e.Type ?? "?", LogRowKind.Info, e.Type ?? "", detail),
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
                string role = data.GetProperty("role").GetString() ?? "";
                string content = data.GetProperty("content").GetString() ?? "";
                return role == "user" ? $"Anh: {content}" : $"Ru: {content}";
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
                string emotion = data.GetProperty("emotion").GetString() ?? "không xác định";
                return $"Cảm xúc: {emotion}";
            }
            catch
            {
                return "Cảm xúc: không xác định";
            }
        }

        private static string FormatVts(JsonElement data)
        {
            try
            {
                string facial = data.GetProperty("facial").GetString() ?? string.Empty;
                string body = data.GetProperty("body").GetString() ?? string.Empty;
                string codes = data.GetProperty("codes").GetString() ?? string.Empty;

                if (!string.IsNullOrEmpty(facial))
                    return $"Biểu cảm: {facial}";
                if (!string.IsNullOrEmpty(body))
                    return $"Cử động: {body}";
                if (!string.IsNullOrEmpty(codes))
                    return $"Codes: {codes}";
                return "Hành động VTS";
            }
            catch
            {
                return "Hành động VTS";
            }
        }

        private void OnFilterToggled(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
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
                Title = "Xoá nhật ký",
                Content = "Xoá toàn bộ nhật ký hoạt động hiện tại?",
                PrimaryButtonText = "Xoá",
                CloseButtonText = "Huỷ",
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
                ApplyFilter();
            }
        }

        public void Dispose()
        {
            _refreshTimer?.Stop();
            _refreshTimer = null;
        }
    }

    public enum LogRowKind
    {
        Chat,
        Emotion,
        Vts,
        Info,
    }

    public sealed class LogRow
    {
        private static readonly SolidColorBrush ChatBrush = FromArgb(0xFF, 0x3C, 0x87, 0xFF);
        private static readonly SolidColorBrush EmotionBrush = FromArgb(0xFF, 0xB0, 0x6E, 0xF0);
        private static readonly SolidColorBrush VtsBrush = FromArgb(0xFF, 0xFF, 0x5C, 0x92);
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
