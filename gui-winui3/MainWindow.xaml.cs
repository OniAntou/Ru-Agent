using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Automation;
using RuApp.Models;
using RuApp.Services;
using RuApp.Views;

namespace RuApp
{
    public sealed partial class MainWindow : Window
    {
        private readonly RuAppComposition _composition;
        private readonly IRuBackendSessionProvider _backendProvider;
        private ApiService? _apiService;
        private VoicePlaybackService? _voicePlayback;
        private SubtitleOverlayService? _subtitleOverlay;
        private SubtitlePresenter? _subtitlePresenter;
        private DispatcherTimer? _observationTimer;
        private string? _lastSeenReactionId;
        private bool _observationPollInFlight;

        private ChatView? _chatView;
        private VoiceView? _voiceView;
        private SettingsView? _settingsView;
        private LogView? _logView;
        private DiagnosticsView? _diagnosticsView;
        private AgentRunController? _agentController;
        private PolicyPanel? _policyControlPanel;
        private AgentPolicyController? _policyController;
        private AuditLogPanel? _auditPanel;
        private DispatcherTimer? _sessionsTimer;
        private RefreshController<SessionListResponse?>? _sessionsRefreshController;
        private ResponsiveLayoutTier? _responsiveTier;
        private double _lastExpandedSidebarWidth = 250;
        private readonly UiThemePreferenceStore _themePreferenceStore = new();
        private Microsoft.UI.Windowing.AppWindow? _appWindow;
        private UiTheme _currentTheme = UiTheme.Light;

        public static MainWindow? Instance { get; private set; }

        public ObservableCollection<SessionInfo> Sessions { get; } = new();

        public MainWindow()
            : this(RuAppComposition.Production())
        {
        }

        public MainWindow(RuAppComposition composition)
        {
            _composition = composition ?? throw new ArgumentNullException(nameof(composition));
            _backendProvider = composition.BackendProvider;
            Instance = this;
            try
            {
                this.InitializeComponent();
                this.Title = "Ru — Conversational Runtime";
                
                try
                {
                    this.ExtendsContentIntoTitleBar = true;
                    this.SetTitleBar(AppTitleBar);

                    var windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
                    _appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
                }
                catch { }

                ApplyTheme(_themePreferenceStore.Load());

                this.Closed += OnWindowClosed;

                _ = InitializeAppAsync();
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.WriteCrashEvent(ex);
                throw;
            }
        }

        private void OnThemeToggleClick(object sender, RoutedEventArgs e)
        {
            var nextTheme = _currentTheme == UiTheme.Dark ? UiTheme.Light : UiTheme.Dark;
            ApplyTheme(nextTheme);
            _themePreferenceStore.TrySave(nextTheme);
        }

        private void ApplyTheme(UiTheme theme)
        {
            _currentTheme = theme;
            ShellRoot.RequestedTheme = theme == UiTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

            bool isDark = theme == UiTheme.Dark;
            var label = isDark ? "Chuyển sang sáng" : "Chuyển sang tối";
            LabelThemeToggle.Text = label;
            ThemeToggleIcon.Glyph = isDark ? "\uE793" : "\uE706";
            ToolTipService.SetToolTip(ThemeToggleButton, label);
            AutomationProperties.SetName(ThemeToggleButton, label);
            ApplyTitleBarTheme(isDark);
        }

        private void ApplyTitleBarTheme(bool isDark)
        {
            if (_appWindow?.TitleBar is not { } titleBar)
            {
                return;
            }

            var foreground = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 238, 242, 235)
                : Microsoft.UI.ColorHelper.FromArgb(255, 32, 35, 31);
            var muted = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 169, 179, 167)
                : Microsoft.UI.ColorHelper.FromArgb(255, 95, 104, 95);
            var hover = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 45, 54, 47)
                : Microsoft.UI.ColorHelper.FromArgb(255, 217, 221, 213);
            var pressed = isDark
                ? Microsoft.UI.ColorHelper.FromArgb(255, 57, 68, 58)
                : Microsoft.UI.ColorHelper.FromArgb(255, 200, 206, 198);

            titleBar.ButtonForegroundColor = foreground;
            titleBar.ButtonHoverForegroundColor = foreground;
            titleBar.ButtonHoverBackgroundColor = hover;
            titleBar.ButtonPressedForegroundColor = foreground;
            titleBar.ButtonPressedBackgroundColor = pressed;
            titleBar.ButtonInactiveForegroundColor = muted;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
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
            StatusText.Text = $"Đang khởi động {_backendProvider.DisplayName} Backend...";
            StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppWarningBrush"];

            string? projectRoot = _composition.IsHarness
                ? _composition.HarnessWorkspacePath ?? string.Empty
                : ResolveProjectRoot();
            if (projectRoot == null)
            {
                StatusText.Text = "Không tìm thấy project root. Hãy đặt RuApp.exe trong hoặc dưới thư mục D:\\LLM.";
                StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
                return;
            }

            BackendSession? backendSession = await _backendProvider.StartAsync(
                projectRoot,
                CancellationToken.None);
            if (backendSession != null)
            {
                StatusText.Text = $"{_backendProvider.DisplayName} Backend đã sẵn sàng (127.0.0.1:{backendSession.HttpBaseUri.Port})";
                StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppSuccessBrush"];
                _backendProvider.BackendExited += OnBackendExited;

                _apiService = new ApiService(backendSession);
                _voicePlayback = new VoicePlaybackService(
                    _apiService,
                    _composition.PlaybackDeviceFactory?.Invoke());

                if (_composition.EnableSubtitleOverlay)
                {
                    var subtitleWindow = new Views.SubtitleOverlayWindow();
                    var subtitleTracker = new Services.VtsWindowTracker(new Services.VtsWindowLocator());
                    _subtitlePresenter = new Services.SubtitlePresenter();
                    _subtitleOverlay = new Services.SubtitleOverlayService(
                        subtitleWindow,
                        subtitleTracker,
                        _subtitlePresenter);
                    _voicePlayback.ConfigureSubtitles(_subtitlePresenter);
                }

                await _voicePlayback.RefreshSettingsAsync();
                var voiceStatus = await _apiService.GetVoiceStatusAsync();
                if (voiceStatus != null && _subtitleOverlay != null)
                {
                    _subtitleOverlay.ApplyCustomization(
                        voiceStatus.SubtitleColor,
                        voiceStatus.SubtitleSize,
                        voiceStatus.SubtitleDuration,
                        voiceStatus.SubtitlePosMode,
                        voiceStatus.SubtitlePosX,
                        voiceStatus.SubtitlePosY);
                }
                if (_voicePlayback.SubtitlesEnabled && _subtitleOverlay != null)
                {
                    _subtitleOverlay.Start();
                }

                _agentController = _composition.CreateAgentRunController(this.DispatcherQueue);

                _chatView = new ChatView();
                _chatView.Initialize(
                    _apiService,
                    _voicePlayback,
                    _composition.VoiceSourceFactory,
                    _composition.HarnessEvents,
                    _composition.HarnessScenario,
                    _agentController,
                    projectRoot);
                _chatView.SessionOpened += (_, e) =>
                {
                    SyncSidebarSelection(e.SessionId);
                };

                _voiceView = new VoiceView();
                _voiceView.Initialize(_apiService, _voicePlayback, _subtitleOverlay);

                _settingsView = new SettingsView();
                _settingsView.Initialize(_apiService);

                _logView = new LogView();
                _logView.Initialize(_apiService);

                _diagnosticsView = new DiagnosticsView();
                _diagnosticsView.Initialize(_apiService);
                _diagnosticsView.SetActive(false);

                _policyController = _composition.CreateAgentPolicyController(this.DispatcherQueue);
                _policyControlPanel = new PolicyPanel { Controller = _policyController };

                _auditPanel = new AuditLogPanel { Log = _composition.AuditLog };

                // The conversation surface is Agent-only.
                ContentFrame.Content = _chatView;
                SetActiveNav(NavChat);
                
                _sessionsRefreshController = new RefreshController<SessionListResponse?>(
                    token => _apiService!.GetSessionsAsync(token),
                    result => { if (result != null) ApplySessions(result); },
                    action => DispatcherQueue.TryEnqueue(() => action()));
                _sessionsRefreshController.SetActive(true);
                await _sessionsRefreshController.RefreshAsync();
                await UpdateHealthStatusAsync();

                _sessionsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _sessionsTimer.Tick += async (_, __) =>
                {
                    if (_sessionsRefreshController != null) await _sessionsRefreshController.RefreshAsync();
                    else await LoadSessionsAsync();
                    try { await UpdateHealthStatusAsync(); } catch { }
                };
                _sessionsTimer.Start();

                if (!_composition.IsHarness)
                {
                    _observationTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                    _observationTimer.Tick += OnObservationTimerTick;
                    _observationTimer.Start();
                }
            }
            else
            {
                StatusText.Text = $"Không thể kết nối {_backendProvider.DisplayName} Backend!";
                var danger = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
                StatusDot.Fill = danger;
                DotBackend.Fill = danger;
                DotWorker.Fill = danger;
                TextBackend.Text = ".NET Backend: Lỗi";
                TextWorker.Text = "AI Worker: Lỗi";
            }
        }

        private void SetActiveNav(Button activeBtn)
        {
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var muted = (SolidColorBrush)Application.Current.Resources["RuMutedBrush"];
            var ink = (SolidColorBrush)Application.Current.Resources["RuInkBrush"];
            var accent = (SolidColorBrush)Application.Current.Resources["RuAccentBrush"];
            var surface = (SolidColorBrush)Application.Current.Resources["RuSurfaceBrush"];

            foreach (var button in new[] { NavChat, NavVoice, NavSettings })
            {
                button.Background = transparent;
                button.Foreground = muted;
                button.BorderBrush = transparent;
                button.BorderThickness = new Thickness(0);
                button.CornerRadius = new CornerRadius(0, 4, 4, 0);
            }

            activeBtn.Background = surface;
            activeBtn.Foreground = ink;
            activeBtn.BorderBrush = accent;
            activeBtn.BorderThickness = new Thickness(2, 0, 0, 0);
            activeBtn.CornerRadius = new CornerRadius(0, 4, 4, 0);
        }

        private void UpdateShellStatusTooltip()
        {
            if (ShellStatusRow == null) return;

            var details = string.Join(
                Environment.NewLine,
                new[] { TextBackend.Text, TextWorker.Text, TextVts.Text }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));

            ToolTipService.SetToolTip(
                ShellStatusRow,
                string.IsNullOrWhiteSpace(details) ? StatusText.Text : details);
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
                    "diagnostics" => _diagnosticsView,
                    "policy" => _policyControlPanel,
                    "audit" => _auditPanel,
                    _ => _chatView
                };
                _diagnosticsView?.SetActive(tag == "diagnostics");
            }
        }

        private void OnToolsMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
            {
                return;
            }

            ContentFrame.Content = tag switch
            {
                "log" => _logView,
                "diagnostics" => _diagnosticsView,
                "policy" => _policyControlPanel,
                "audit" => _auditPanel,
                _ => _chatView
            };
            _diagnosticsView?.SetActive(tag == "diagnostics");
        }

        public void NavigateToDiagnostics(string? correlationId = null)
        {
            ContentFrame.Content = _diagnosticsView;
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                _diagnosticsView?.FilterByCorrelation(correlationId);
            }
        }


        private void OnBackendExited(object? sender, EventArgs e)
        {
            if (DispatcherQueue == null)
            {
                return;
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                var dangerBrush = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
                var mutedBrush = (SolidColorBrush)Application.Current.Resources["AppTextTertiaryBrush"];
                StatusText.Text = $"{_backendProvider.DisplayName} Backend đã dừng.";
                StatusDot.Fill = dangerBrush;
                DotBackend.Fill = dangerBrush;
                DotWorker.Fill = dangerBrush;
                DotVts.Fill = mutedBrush;
                TextBackend.Text = ".NET Backend: Đã dừng";
                TextWorker.Text = "AI Worker: Đã dừng";
                TextVts.Text = "VTS: Đã ngắt";
                UpdateShellStatusTooltip();
            });
        }

        private async void OnWindowClosed(object sender, WindowEventArgs args)
        {
            _sessionsTimer?.Stop();
            _sessionsRefreshController?.SetActive(false);
            if (_sessionsRefreshController != null) await _sessionsRefreshController.DisposeAsync();
            _sessionsRefreshController = null;
            _observationTimer?.Stop();
            _chatView?.Dispose();
            _settingsView?.Dispose();
            _voiceView?.Dispose();
            _logView?.Dispose();
            _diagnosticsView?.Dispose();
            _subtitleOverlay?.Dispose();
            _subtitlePresenter?.Dispose();
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
            _apiService?.Dispose();
            _backendProvider.BackendExited -= OnBackendExited;
            _agentController?.Dispose();
            _agentController = null;
            _policyController?.Dispose();
            _policyController = null;
            _policyControlPanel = null;
            _auditPanel = null;
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
            _composition.Dispose();
        }

        private async Task LoadSessionsAsync()
        {
            if (_apiService == null) return;
            if (_sessionsRefreshController != null)
            {
                await _sessionsRefreshController.RefreshAsync();
                return;
            }
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
            if (_sessionsRefreshController != null)
            {
                try { await _sessionsRefreshController.RefreshAsync(); } catch { }
            }
            else
            {
                try
                {
                    var result = await _apiService.GetSessionsAsync();
                    if (result != null) ApplySessions(result);
                }
                catch { }
            }

            try
            {
                await UpdateHealthStatusAsync();
            }
            catch { }
        }

        private async void OnObservationTimerTick(object? sender, object e)
        {
            if (_apiService == null || _voicePlayback == null || _observationPollInFlight) return;
            _observationPollInFlight = true;
            try
            {
                var status = await _apiService.GetObservationStatusAsync();
                if (status != null
                    && status.Active
                    && !string.IsNullOrWhiteSpace(status.LastReaction)
                    && !string.IsNullOrWhiteSpace(status.LastReactionId)
                    && !string.Equals(_lastSeenReactionId, status.LastReactionId, StringComparison.Ordinal))
                {
                    if (status.LastReactionAt is double reactionAt
                        && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - reactionAt > 15.0)
                    {
                        _lastSeenReactionId = status.LastReactionId;
                        _ = _apiService.AcknowledgeObservationAsync(
                            status.LastReactionId,
                            played: false);
                        return;
                    }
                    bool played = await _voicePlayback.SpeakTextAsync(
                        status.LastReaction,
                        interrupt: false);
                    if (played)
                    {
                        _lastSeenReactionId = status.LastReactionId;
                        _ = _apiService.AcknowledgeObservationAsync(
                            status.LastReactionId,
                            played: true);
                    }
                }
            }
            catch { }
            finally
            {
                _observationPollInFlight = false;
            }
        }

        private async Task UpdateHealthStatusAsync()
        {
            if (_apiService == null) return;

            var successBrush = (SolidColorBrush)Application.Current.Resources["AppSuccessBrush"];
            var warningBrush = (SolidColorBrush)Application.Current.Resources["AppWarningBrush"];
            var dangerBrush = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
            var mutedBrush = (SolidColorBrush)Application.Current.Resources["AppTextTertiaryBrush"];

            // 1. .NET Backend status
            bool backendRunning = _backendProvider.Session != null;
            DotBackend.Fill = backendRunning ? successBrush : dangerBrush;
            TextBackend.Text = backendRunning ? ".NET Backend: OK" : ".NET Backend: Lỗi";

            // 2. Python Worker status
            bool workerOk = false;
            string? workerError = null;
            if (backendRunning)
            {
                (workerOk, workerError) = await _apiService.CheckWorkerHealthAsync();
            }
            DotWorker.Fill = workerOk ? successBrush : (backendRunning ? warningBrush : dangerBrush);
            TextWorker.Text = workerOk
                ? "AI Worker: Sẵn sàng"
                : backendRunning && workerError != null
                    ? $"AI Worker: Lỗi ({workerError})"
                    : backendRunning
                        ? "AI Worker: Đang đợi..."
                        : "AI Worker: Tắt";

            // 3. VTube Studio status
            bool vtsConnected = false;
            if (!_composition.IsHarness && workerOk)
            {
                var vtsStatus = await _apiService.GetVtsStatusAsync();
                vtsConnected = vtsStatus?.Connected == true;
            }
            DotVts.Fill = vtsConnected ? successBrush : mutedBrush;
            TextVts.Text = _composition.IsHarness
                ? "VTS: Bỏ qua trong Harness"
                : vtsConnected ? "VTS: Đã kết nối" : "VTS: Chưa mở";

            // Overall summary text
            if (!backendRunning)
            {
                StatusDot.Fill = dangerBrush;
                StatusText.Text = "Không thể kết nối .NET Backend!";
            }
            else if (!workerOk)
            {
                StatusDot.Fill = warningBrush;
                StatusText.Text = "AI Worker đang khởi động hoặc chưa sẵn sàng";
            }
            else
            {
                StatusDot.Fill = successBrush;
                StatusText.Text = _composition.IsHarness
                    ? "Harness đã sẵn sàng"
                    : vtsConnected ? "Hệ thống sẵn sàng (Đã kết nối Live2D)" : "Hệ thống đã sẵn sàng";
            }

            UpdateShellStatusTooltip();
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

        private void OnShellPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            var ctrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

            if (!ctrlDown) return;

            if (e.Key == Windows.System.VirtualKey.N)
            {
                e.Handled = true;
                OnNewChatClick(NavNewChat, new RoutedEventArgs());
            }
            else if (e.Key == Windows.System.VirtualKey.Number1)
            {
                e.Handled = true;
                OnNavClick(NavChat, new RoutedEventArgs());
            }
            else if (e.Key == Windows.System.VirtualKey.Number2)
            {
                e.Handled = true;
                OnNavClick(NavVoice, new RoutedEventArgs());
            }
            else if (e.Key == Windows.System.VirtualKey.Number3)
            {
                e.Handled = true;
                OnNavClick(NavSettings, new RoutedEventArgs());
            }
        }

        private async void OnDeleteSessionClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string sessionId && _apiService != null)
            {
                if (ShellRoot.XamlRoot != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Xác nhận xoá đoạn chat",
                        Content = "Bạn có chắc chắn muốn xoá vĩnh viễn đoạn chat này không?",
                        PrimaryButtonText = "Xoá",
                        CloseButtonText = "Huỷ",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = ShellRoot.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result != ContentDialogResult.Primary)
                    {
                        return;
                    }
                }

                bool deleted = await _apiService.DeleteSessionAsync(sessionId);
                if (!deleted)
                {
                    if (ShellRoot.XamlRoot != null)
                    {
                        var errorDialog = new ContentDialog
                        {
                            Title = "Không thể xoá đoạn chat",
                            Content = "Backend chưa xoá được đoạn chat này. Hãy thử lại sau.",
                            CloseButtonText = "Đóng",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = ShellRoot.XamlRoot
                        };
                        await errorDialog.ShowAsync();
                    }

                    return;
                }

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

        private void RootLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width <= 0)
            {
                return;
            }

            ApplyResponsiveShell(e.NewSize.Width);
        }

        private void ApplyResponsiveShell(double width)
        {
            var metrics = ResponsiveLayoutPolicy.GetForWidth(width);
            if (_responsiveTier == metrics.Tier)
            {
                return;
            }

            if (metrics.Tier == ResponsiveLayoutTier.Narrow)
            {
                if (_responsiveTier is not null && SidebarColumn.ActualWidth > 0)
                {
                    _lastExpandedSidebarWidth = SidebarColumn.ActualWidth;
                }

                SidebarColumn.MinWidth = metrics.MinSidebarWidth;
                SidebarColumn.MaxWidth = metrics.MaxSidebarWidth;
                SidebarColumn.Width = new GridLength(metrics.NarrowRailWidth);
            }
            else
            {
                SidebarColumn.MinWidth = metrics.MinSidebarWidth;
                SidebarColumn.MaxWidth = metrics.MaxSidebarWidth;
                var restoredWidth = _responsiveTier == ResponsiveLayoutTier.Narrow
                    ? _lastExpandedSidebarWidth
                    : SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : metrics.DefaultSidebarWidth;
                SidebarColumn.Width = new GridLength(
                    ResponsiveLayoutPolicy.ClampSidebarWidth(restoredWidth, metrics));
            }

            _responsiveTier = metrics.Tier;
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
