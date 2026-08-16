using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using RuApp.Models;
using RuApp.Services;
using RuApp.Views;

namespace RuApp
{
    public sealed partial class MainWindow : Window
    {
        private readonly BackendProcessManager _processManager;
        private ApiService? _apiService;
        private ChatWebSocketClient? _wsClient;
        private VoicePlaybackService? _voicePlayback;
        private SubtitleOverlayService? _subtitleOverlay;

        private ChatView? _chatView;
        private VoiceView? _voiceView;
        private SettingsView? _settingsView;
        private LogView? _logView;
        private DispatcherTimer? _sessionsTimer;

        public ObservableCollection<SessionInfo> Sessions { get; } = new();

        public MainWindow()
        {
            try
            {
                this.InitializeComponent();
                this.Title = "Ru — Local LFM2.5 Conversational Runtime";
                
                try
                {
                    this.ExtendsContentIntoTitleBar = true;
                    this.SetTitleBar(AppTitleBar);

                    var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
                    var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);

                    if (appWindow != null && appWindow.TitleBar != null)
                    {
                        appWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 248, 250, 252);
                        appWindow.TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                        appWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 38, 43, 56);
                        appWindow.TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                        appWindow.TitleBar.ButtonPressedBackgroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 52, 59, 76);
                        appWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.ColorHelper.FromArgb(255, 140, 145, 160);
                        appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                        appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                    }
                }
                catch { }

                _processManager = new BackendProcessManager();
                this.Closed += OnWindowClosed;

                _ = InitializeAppAsync();
            }
            catch (Exception ex)
            {
                string msg = $"MainWindow Constructor Exception:\n{ex}\n--- Inner ---\n{ex.InnerException}";
                try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt"), msg); } catch { }
                throw;
            }
        }

        private static string? ResolveProjectRoot()
        {
            string? envRoot = Environment.GetEnvironmentVariable("RU_PROJECT_ROOT");
            if (!string.IsNullOrWhiteSpace(envRoot) && Directory.Exists(envRoot) && File.Exists(Path.Combine(envRoot, "pyproject.toml")))
            {
                return Path.GetFullPath(envRoot);
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string candidate = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", ".."));
            if (File.Exists(Path.Combine(candidate, "pyproject.toml")))
            {
                return candidate;
            }

            string? walk = candidate;
            while (walk != null && walk.Length > 3)
            {
                if (File.Exists(Path.Combine(walk, "pyproject.toml")))
                {
                    return walk;
                }
                walk = Path.GetDirectoryName(walk);
            }

            return null;
        }

        private async Task InitializeAppAsync()
        {
            SidebarSessionList.ItemsSource = Sessions;
            StatusText.Text = $"Đang khởi động {_processManager.DisplayName} Backend...";
            StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppWarningBrush"];

            string? projectRoot = ResolveProjectRoot();
            if (projectRoot == null)
            {
                StatusText.Text = "Không tìm thấy project root. Hãy đặt RuApp.exe trong hoặc dưới thư mục D:\\LLM.";
                StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
                return;
            }

            bool ok = await _processManager.StartBackendAsync(projectRoot);
            if (ok)
            {
                StatusText.Text = $"{_processManager.DisplayName} Backend đã sẵn sàng (127.0.0.1:8000)";
                StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppSuccessBrush"];

                _apiService = new ApiService(_processManager.AuthSecret, _processManager.Port);
                _wsClient = new ChatWebSocketClient(_processManager.AuthSecret, _processManager.Port);
                _wsClient.Start();
                _voicePlayback = new VoicePlaybackService(_apiService);
                await _voicePlayback.RefreshSettingsAsync();

                var subtitleWindow = new Views.SubtitleOverlayWindow();
                var subtitleTracker = new Services.VtsWindowTracker(new Services.VtsWindowLocator());
                var subtitlePresenter = new Services.SubtitlePresenter();
                _subtitleOverlay = new Services.SubtitleOverlayService(subtitleWindow, subtitleTracker, subtitlePresenter);
                _voicePlayback.ConfigureSubtitles(subtitlePresenter);

                _chatView = new ChatView();
                _chatView.Initialize(_apiService, _wsClient, _voicePlayback);
                _chatView.SessionOpened += (_, e) => SyncSidebarSelection(e.SessionId);

                _voiceView = new VoiceView();
                _voiceView.Initialize(_apiService, _voicePlayback, _subtitleOverlay);

                _settingsView = new SettingsView();
                _settingsView.Initialize(_apiService);

                _logView = new LogView();
                _logView.Initialize(_apiService);

                // Default: Chat
                ContentFrame.Content = _chatView;
                SetActiveNav(NavChat);
                
                await LoadSessionsAsync();

                _sessionsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _sessionsTimer.Tick += OnSessionsTimerTick;
                _sessionsTimer.Start();
            }
            else
            {
                StatusText.Text = $"Không thể kết nối {_processManager.DisplayName} Backend!";
                if (!string.IsNullOrWhiteSpace(_processManager.LastError))
                {
                    StatusText.Text += $" {_processManager.LastError}";
                }
                StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
            }
        }

        private void SetActiveNav(Button activeBtn)
        {
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var activeBg = (SolidColorBrush)Application.Current.Resources["AppCardBackgroundHoverBrush"];
            var textPrimary = (SolidColorBrush)Application.Current.Resources["AppTextPrimaryBrush"];
            var textSecondary = (SolidColorBrush)Application.Current.Resources["AppTextSecondaryBrush"];

            NavChat.Background = transparent;
            NavChat.Foreground = textSecondary;
            NavVoice.Background = transparent;
            NavVoice.Foreground = textSecondary;
            NavSettings.Background = transparent;
            NavSettings.Foreground = textSecondary;
            NavLog.Background = transparent;
            NavLog.Foreground = textSecondary;

            activeBtn.Background = activeBg;
            activeBtn.Foreground = textPrimary;
        }

        private void OnNavClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                SetActiveNav(btn);

                ContentFrame.Content = tag switch
                {
                    "chat" => _chatView,
                    "voice" => _voiceView,
                    "settings" => _settingsView,
                    "log" => _logView,
                    _ => _chatView
                };
            }
        }

        private async void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _sessionsTimer?.Stop();
            _settingsView?.Dispose();
            _logView?.Dispose();
            _subtitleOverlay?.Dispose();
            if (_voicePlayback != null)
            {
                try
                {
                    await _voicePlayback.DisposeAsync();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Voice shutdown failed: {ex.Message}");
                }
            }
            _wsClient?.Dispose();
            _apiService?.Dispose();
            _processManager.Dispose();
        }

        private async Task LoadSessionsAsync()
        {
            if (_apiService == null) return;
            var result = await _apiService.GetSessionsAsync();
            if (result != null)
            {
                ApplySessions(result);
            }
        }

        private void ApplySessions(SessionListResponse result)
        {
            var seen = new HashSet<string>();
            int insertAt = 0;
            foreach (var incoming in result.Sessions)
            {
                if (incoming.MessageCount <= 0) continue;
                seen.Add(incoming.Id);
                var existing = Sessions.FirstOrDefault(s => s.Id == incoming.Id);
                if (existing != null)
                {
                    existing.Title = incoming.Title;
                    existing.CreatedAt = incoming.CreatedAt;
                    existing.UpdatedAt = incoming.UpdatedAt;
                    int currentIndex = Sessions.IndexOf(existing);
                    if (currentIndex != insertAt)
                    {
                        Sessions.Move(currentIndex, insertAt);
                    }
                }
                else
                {
                    Sessions.Insert(insertAt, incoming);
                }
                insertAt++;
            }

            for (int i = Sessions.Count - 1; i >= 0; i--)
            {
                if (!seen.Contains(Sessions[i].Id))
                {
                    Sessions.RemoveAt(i);
                }
            }
        }

        private void SyncSidebarSelection(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            foreach (var item in Sessions)
            {
                if (item.Id == sessionId)
                {
                    if (!ReferenceEquals(SidebarSessionList.SelectedItem, item))
                    {
                        SidebarSessionList.SelectedItem = item;
                    }
                    return;
                }
            }
        }

        private async void OnSessionsTimerTick(object? sender, object e)
        {
            if (_apiService == null) return;
            var result = await _apiService.GetSessionsAsync();
            if (result != null)
            {
                ApplySessions(result);
            }
        }

        private async void OnNewChatClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null || _chatView == null) return;
            var session = await _apiService.CreateSessionAsync();
            if (session != null)
            {
                await LoadSessionsAsync();
                
                // Select Chat tab if not selected
                OnNavClick(NavChat, new RoutedEventArgs());
                
                // Tell ChatView to switch to this new session
                await _chatView.OpenSessionAsync(session.Id, session.Title);
            }
        }

        private async void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SidebarSessionList.SelectedItem is SessionInfo selected && _chatView != null)
            {
                // Select Chat tab if not selected
                OnNavClick(NavChat, new RoutedEventArgs());
                await _chatView.OpenSessionAsync(selected.Id, selected.Title);
            }
        }

        private async void OnDeleteSessionClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string sessionId && _apiService != null)
            {
                await _apiService.DeleteSessionAsync(sessionId);
                await LoadSessionsAsync();

                // Nếu xóa hết session thì tạo mới
                if (Sessions.Count == 0)
                {
                    OnNewChatClick(this, new RoutedEventArgs());
                }
            }
        }

        private void SessionItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var deleteBtn = grid.Children.OfType<Button>().FirstOrDefault();
                if (deleteBtn != null)
                {
                    deleteBtn.Opacity = 1.0;
                }
            }
        }

        private void SessionItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid)
            {
                var deleteBtn = grid.Children.OfType<Button>().FirstOrDefault();
                if (deleteBtn != null)
                {
                    deleteBtn.Opacity = 0.0;
                }
            }
        }

        private bool _isResizing = false;
        private double _startWidth = 0;
        private double _startX = 0;

        private void Resizer_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isResizing = true;
            _startX = e.GetCurrentPoint(null).Position.X;
            _startWidth = SidebarColumn.ActualWidth;
            ((UIElement)sender).CapturePointer(e.Pointer);
        }

        private void Resizer_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isResizing)
            {
                double currentX = e.GetCurrentPoint(null).Position.X;
                double diff = currentX - _startX;
                double newWidth = _startWidth + diff;

                if (newWidth >= SidebarColumn.MinWidth && newWidth <= SidebarColumn.MaxWidth)
                {
                    SidebarColumn.Width = new GridLength(newWidth);
                }
            }
        }

        private void Resizer_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isResizing = false;
            ((UIElement)sender).ReleasePointerCapture(e.Pointer);
        }
    }
}
