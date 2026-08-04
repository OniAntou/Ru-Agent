using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
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
        private ScreenObserverService? _observerService;
        private VoicePlaybackService? _voicePlayback;

        private ChatView? _chatView;
        private VoiceView? _voiceView;
        private ObservationControlView? _observationView;
        private SettingsView? _settingsView;
        private DispatcherTimer? _sessionsTimer;


        public ObservableCollection<SessionInfo> Sessions { get; } = new();

        public MainWindow()
        {
            try
            {
                this.InitializeComponent();
                this.Title = "Ru — Local Llama Conversational Runtime";

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

        private async Task InitializeAppAsync()
        {
            SidebarSessionList.ItemsSource = Sessions;
            StatusText.Text = "Đang khởi động Backend FastAPI...";
            StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Orange);

            string projectRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
            if (!File.Exists(Path.Combine(projectRoot, "pyproject.toml")))
            {
                projectRoot = @"D:\LLM";
            }

            bool ok = await _processManager.StartBackendAsync(projectRoot);
            if (ok)
            {
                StatusText.Text = "FastAPI Backend đã sẵn sàng (127.0.0.1:8000)";
                StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Green);

                _apiService = new ApiService(_processManager.AuthSecret, _processManager.Port);
                _wsClient = new ChatWebSocketClient(_processManager.AuthSecret, _processManager.Port);
                _wsClient.Start();
                _observerService = new ScreenObserverService(_apiService);
                _voicePlayback = new VoicePlaybackService(_apiService);
                await _voicePlayback.RefreshSettingsAsync();

                _chatView = new ChatView();
                _chatView.Initialize(_apiService, _wsClient, _voicePlayback);
                _chatView.SessionOpened += (_, e) => SyncSidebarSelection(e.SessionId);

                _voiceView = new VoiceView();
                _voiceView.Initialize(_apiService, _voicePlayback);

                _observationView = new ObservationControlView();
                _observationView.Initialize(_observerService);

                _settingsView = new SettingsView();
                _settingsView.Initialize(_apiService);

                // Default: Chat
                ContentFrame.Content = _chatView;
                NavChat.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(40, 255, 255, 255));
                
                await LoadSessionsAsync();

                _sessionsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _sessionsTimer.Tick += OnSessionsTimerTick;
                _sessionsTimer.Start();
            }
            else
            {
                StatusText.Text = "Không thể kết nối FastAPI Backend!";
                StatusDot.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red);
            }
        }

        private void OnNavClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                // Reset all nav button backgrounds
                var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                var active = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(40, 255, 255, 255));

                NavChat.Background = transparent;
                NavVoice.Background = transparent;
                NavObservation.Background = transparent;
                NavSettings.Background = transparent;

                btn.Background = active;

                ContentFrame.Content = tag switch
                {
                    "chat" => _chatView,
                    "voice" => _voiceView,
                    "observation" => _observationView,
                    "settings" => _settingsView,
                    _ => _chatView
                };
            }
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _wsClient?.Dispose();
            _processManager.Dispose();
        }

        // Hamburger menu logic removed

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
                // Hide sessions that have never been chatted in; they only
                // appear in the recent list once a message exists.
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
            if (sender is Grid grid && grid.Children.Count > 1 && grid.Children[1] is Button deleteBtn)
            {
                deleteBtn.Visibility = Visibility.Visible;
            }
        }

        private void SessionItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Grid grid && grid.Children.Count > 1 && grid.Children[1] is Button deleteBtn)
            {
                deleteBtn.Visibility = Visibility.Collapsed;
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
