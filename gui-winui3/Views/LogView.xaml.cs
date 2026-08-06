using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class LogView : UserControl
    {
        private ApiService? _apiService;
        private DispatcherTimer? _refreshTimer;
        public ObservableCollection<string> Lines { get; } = new();
        private static readonly TimeZoneInfo VietnamZone = TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time");

        public LogView()
        {
            this.InitializeComponent();
        }

        public void Initialize(ApiService apiService)
        {
            _apiService = apiService;
            Lines.Clear();
            Lines.Add("Đang tải nhật ký...");
            _ = RefreshAsync();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _refreshTimer.Tick += async (_, __) => await RefreshAsync();
            _refreshTimer.Start();
        }

        private async Task RefreshAsync()
        {
            if (_apiService == null) return;
            var events = await _apiService.GetActivityLogAsync(200);
            if (events == null) return;

            var nowUtc = DateTimeOffset.UtcNow;
            var nowVietnam = TimeZoneInfo.ConvertTimeFromUtc(nowUtc.UtcDateTime, VietnamZone);
            var threshold = new DateTimeOffset(nowVietnam.Year, nowVietnam.Month, nowVietnam.Day, nowVietnam.Hour, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

            var fresh = events.Where(e => e.Ts * 1000 >= threshold).ToList();

            Lines.Clear();
            if (!fresh.Any())
            {
                Lines.Add($"[{TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.UtcNow.UtcDateTime, VietnamZone):HH:mm:ss}] Chưa có hoạt động nào trong giờ này.");
            }
            else
            {
                foreach (var e in fresh)
                {
                    string time;
                    try { time = TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds((long)e.Ts).UtcDateTime, VietnamZone).ToString("HH:mm:ss"); }
                    catch { time = "--:--:--"; }

                    string prefix = e.Type switch
                    {
                        "chat" => "[CHAT]",
                        "emotion" => "[EMO]",
                        "vts_action" => "[VTS]",
                        _ => "[???]",
                    };

                    string content = e.Type switch
                    {
                        "chat" => FormatChat(e.Data),
                        "emotion" => FormatEmotion(e.Data),
                        "vts_action" => FormatVts(e.Data),
                        _ => e.Type ?? "",
                    };

                    Lines.Add($"[{time}] {prefix} {content}");
                }
            }
        }

        private static string FormatChat(JsonElement data)
        {
            try
            {
                string role = data.GetProperty("role").GetString() ?? "";
                string content = data.GetProperty("content").GetString() ?? "";
                return role == "user" ? $"Anh: {content}" : $"Ru: {content}";
            }
            catch { return ""; }
        }

        private static string FormatEmotion(JsonElement data)
        {
            try { return $"Cảm xúc: {data.GetProperty("emotion").GetString()}"; }
            catch { return ""; }
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
            catch { return "Hành động VTS"; }
        }
    }
}
