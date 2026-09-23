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
using RuAgent.Hosting;
using RuAgent.SessionInterchange;
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
        private HealthStatusService? _healthStatusService;
        private readonly StartupCancellationScope _startup = new();
        private Task _startupTask = Task.CompletedTask;
        private bool _closed;

        private ChatView? _chatView;
        private SettingsView? _settingsView;
        private LogView? _logView;
        private DiagnosticsView? _diagnosticsView;
        private AgentRunController? _agentController;
        private PolicyPanel? _policyControlPanel;
        private AgentPolicyController? _policyController;
        private AuditLogPanel? _auditPanel;
        private DispatcherTimer? _sessionsTimer;
        private bool _sessionsPollInFlight;
        private RefreshController<SessionListResponse?>? _sessionsRefreshController;
        private RefreshController<HealthStatusSnapshot?>? _healthRefreshController;
        private ProviderCatalogController? _providerCatalogController;
        private GroupedProviderCatalogController? _groupedCatalogs;
        private DispatcherTimer? _providerCatalogTimer;
        private ResponsiveLayoutTier? _responsiveTier;
        private double _lastExpandedSidebarWidth = 250;
        private readonly UiThemePreferenceStore _themePreferenceStore = new();
        private Microsoft.UI.Windowing.AppWindow? _appWindow;
        private UiTheme _currentTheme = UiTheme.Light;
        private bool _reconnecting;
        private DispatcherTimer? _reconnectTimer;

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
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] TitleBar setup failed: {ex.Message}");
                }

                ApplyTheme(_themePreferenceStore.Load());
                InitializeSidebarHoverEffects();

                this.Closed += OnWindowClosed;

                _startupTask = InitializeAppSafelyAsync();
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
            var label = isDark ? "Switch to light mode" : "Switch to dark mode";
            LabelThemeToggle.Text = label;
            ThemeToggleIcon.Glyph = isDark ? "\uE793" : "\uE706";
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

            var cwd = Directory.GetCurrentDirectory();
            if (File.Exists(Path.Combine(cwd, "pyproject.toml")))
            {
                return Path.GetFullPath(cwd);
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

            // No repository anywhere above us, which is the normal case for an
            // installed copy. Returning null here stopped startup entirely; the
            // directory holding the application is the only root that exists, and the
            // caller picks the actual workspace.
            return RuAppLayout.ResolveWorkingRoot(projectRoot: null, baseDir);
        }

        private async Task InitializeAppSafelyAsync()
        {
            try { await InitializeAppAsync(); }
            catch (OperationCanceledException) when (_startup.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.WriteCrashEvent(ex);
                if (!_closed) StatusText.Text = "Could not start Ru. Please check the logs.";
            }
        }

        private async Task InitializeAppAsync()
        {
            SidebarSessionList.ItemsSource = _sidebarRows;
            StatusText.Text = $"Starting {_backendProvider.DisplayName} Backend...";
            StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppWarningBrush"];
            WelcomeOverlay?.SetStatus($"Starting {_backendProvider.DisplayName}...", isReady: false);

            string? projectRoot = _composition.IsHarness
                ? _composition.HarnessWorkspacePath ?? string.Empty
                : ResolveProjectRoot();
            if (projectRoot == null)
            {
                StatusText.Text = "Project root not found. Please place RuApp.exe inside or under D:\\LLM.";
                StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
                WelcomeOverlay?.SetStatus("Project root not found. Click to continue.", isReady: true, isError: true);
                return;
            }

            BackendSession? backendSession = await _startup.RunAsync(token => _backendProvider.StartAsync(projectRoot, token));
            if (backendSession != null)
            {
                StatusText.Text = $"{_backendProvider.DisplayName} Backend ready (127.0.0.1:{backendSession.HttpBaseUri.Port})";
                StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppSuccessBrush"];
                WelcomeOverlay?.NotifyReady();
                _backendProvider.BackendExited += OnBackendExited;

                _apiService = new ApiService(backendSession);
                _apiService.RequestFailed += (_, args) =>
                {
                    var err = args.Error;
                    System.Diagnostics.Debug.WriteLine($"[ApiService.RequestFailed] {err.Method} status={err.StatusCode} {err.ErrorType}: {err.Message}");
                    GuiDiagnosticWriter.WriteApiError(err.Method, err.ErrorType, err.StatusCode);
                };
                var catalogService = new ProviderCatalogService(_apiService);
                _providerCatalogController = new ProviderCatalogController(catalogService);
                _groupedCatalogs = new GroupedProviderCatalogController(catalogService);
                _apiService.SettingsSaved += OnSettingsSaved;
                _providerCatalogTimer = new DispatcherTimer { Interval = ProviderCatalogController.PollInterval };
                _providerCatalogTimer.Tick += async (_, __) =>
                {
                    await (_providerCatalogController?.RefreshIfDueAsync() ?? Task.FromResult(false));
                    await (_groupedCatalogs?.RefreshIfDueAsync() ?? Task.FromResult(false));
                };
                _healthStatusService = new HealthStatusService(
                    _apiService,
                    () => _backendProvider.Session != null);

                _agentController = _composition.CreateAgentRunController(this.DispatcherQueue);

                _chatView = new ChatView();
                _chatView.Initialize(
                    _apiService,
                    _composition.HarnessEvents,
                    _composition.HarnessScenario,
                    _agentController,
                    projectRoot,
                    _providerCatalogController,
                    _groupedCatalogs);
                _chatView.SessionOpened += OnChatSessionOpened;
                _chatView.SessionForked += OnChatSessionForked;
                // A failure message that offers to open provider settings needs the
                // window: the settings surface belongs to it, not to the transcript.
                _chatView.ProviderSettingsRequested += OnProviderSettingsRequested;

                _settingsView = new SettingsView();
                _settingsView.Initialize(_apiService, _providerCatalogController);
                _settingsView.SetCurrentTheme(_currentTheme);
                if (_chatView is not null)
                {
                    _settingsView.SetCurrentPreset(_chatView.CurrentAgentPreset);
                }
                _settingsView.ThemeChanged += nextTheme =>
                {
                    ApplyTheme(nextTheme);
                    _themePreferenceStore.TrySave(nextTheme);
                };
                _settingsView.PresetChanged += nextPreset =>
                {
                    _chatView?.SetAgentPreset(nextPreset);
                };
                SettingsModalContent.Content = _settingsView;

                _logView = new LogView();
                _logView.Initialize(_apiService);
                _logView.SetActive(false);

                _diagnosticsView = new DiagnosticsView();
                _diagnosticsView.Initialize(_apiService);
                _diagnosticsView.SetActive(false);

                _policyController = _composition.CreateAgentPolicyController(this.DispatcherQueue);
                _policyControlPanel = new PolicyPanel { Controller = _policyController };

                _auditPanel = new AuditLogPanel { Log = _composition.AuditLog };

                // The conversation surface is Agent-only.
                ContentFrame.Content = _chatView;
                SetActiveNav(NavChat);
                _providerCatalogController.SetPollingActive(true);
                _groupedCatalogs?.SetPollingActive(true);
                _ = _groupedCatalogs?.RefreshIfDueAsync();
                _providerCatalogTimer.Start();
                
                _sessionsRefreshController = new RefreshController<SessionListResponse?>(
                    token => _apiService!.GetSessionsAsync(token),
                    result => { if (result != null) ApplySessions(result); },
                    action =>
                    {
                        if (!DispatcherQueue.TryEnqueue(() => action()))
                        {
                            throw new OperationCanceledException("The window dispatcher rejected a refresh callback.");
                        }
                    });
                _sessionsRefreshController.SetActive(true);
                await _startup.RunAsync(_ => _sessionsRefreshController.RefreshAsync());

                _healthRefreshController = new RefreshController<HealthStatusSnapshot?>(
                    async token => await _healthStatusService!.GatherAsync(token),
                    snapshot => { if (snapshot != null) ApplyHealthStatus(snapshot); },
                    action =>
                    {
                        if (!DispatcherQueue.TryEnqueue(() => action()))
                        {
                            throw new OperationCanceledException("The window dispatcher rejected a health callback.");
                        }
                    });
                _healthRefreshController.SetActive(true);
                await _startup.RunAsync(_ => _healthRefreshController.RefreshAsync());

                _sessionsTimer = new DispatcherTimer { Interval = Services.UiTiming.SessionsPoll };
                _sessionsTimer.Tick += async (_, __) =>
                {
                    if (_sessionsPollInFlight) return;
                    _sessionsPollInFlight = true;
                    try
                    {
                        if (_sessionsRefreshController != null) await _sessionsRefreshController.RefreshAsync();
                        else await LoadSessionsAsync();
                        try { await UpdateHealthStatusAsync(); }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[MainWindow] Health poll failed: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainWindow] Sessions poll failed: {ex.Message}");
                    }
                    finally
                    {
                        _sessionsPollInFlight = false;
                    }
                };
                _sessionsTimer.Start();

                if (!_composition.IsHarness)
                {
                }

                if (_composition.IsHarness && WelcomeOverlay != null && !WelcomeOverlay.IsDismissed)
                {
                    _ = WelcomeOverlay.DismissAsync();
                }
            }
            else
            {
                StatusText.Text = $"Could not connect to {_backendProvider.DisplayName} Backend!";
                var danger = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
                StatusDot.Fill = danger;
                WelcomeOverlay?.NotifyReady(isError: true);
            }
        }

        private Button? _activeNavButton;

        private void InitializeSidebarHoverEffects()
        {
            SetupNavHover(NavChat);
            SetupNavHover(NavSettings);

            // Theme toggle button
            ThemeToggleButton.PointerEntered += (s, e) =>
            {
                ThemeToggleButton.Background = (Brush)Application.Current.Resources["RuCanvasBrush"];
                ThemeToggleButton.Foreground = (Brush)Application.Current.Resources["RuInkBrush"];
            };
            ThemeToggleButton.PointerExited += (s, e) =>
            {
                ThemeToggleButton.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                ThemeToggleButton.Foreground = (Brush)Application.Current.Resources["RuMutedBrush"];
            };
        }

        private void SetupNavHover(Button btn)
        {
            btn.PointerEntered += (s, e) =>
            {
                if (btn != _activeNavButton && (btn != NavSettings || !_isSettingsModalOpen))
                {
                    btn.Background = (Brush)Application.Current.Resources["RuCanvasBrush"];
                    btn.Foreground = (Brush)Application.Current.Resources["RuInkBrush"];
                }
            };
            btn.PointerExited += (s, e) =>
            {
                if (btn != _activeNavButton && (btn != NavSettings || !_isSettingsModalOpen))
                {
                    btn.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    btn.Foreground = (Brush)Application.Current.Resources["RuMutedBrush"];
                }
            };
        }

        private void SetActiveNav(Button activeBtn)
        {
            _activeNavButton = activeBtn;
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var muted = (SolidColorBrush)Application.Current.Resources["RuMutedBrush"];
            var ink = (SolidColorBrush)Application.Current.Resources["RuInkBrush"];
            var surface = (SolidColorBrush)Application.Current.Resources["RuSurfaceBrush"];
            var line = (SolidColorBrush)Application.Current.Resources["RuLineBrush"];

            foreach (var button in new[] { NavChat, NavSettings })
            {
                button.Background = transparent;
                button.Foreground = muted;
                button.BorderBrush = transparent;
                button.BorderThickness = new Thickness(0);
                button.CornerRadius = new CornerRadius(6);
            }

            activeBtn.Background = surface;
            activeBtn.Foreground = ink;
            activeBtn.BorderBrush = line;
            activeBtn.BorderThickness = new Thickness(1);
            activeBtn.CornerRadius = new CornerRadius(6);
        }

        private void OnChatSessionOpened(object? sender, SessionOpenedEventArgs e)
        {
            SyncSidebarSelection(e.SessionId);
        }

        private async void OnChatSessionForked(object? sender, SessionInfo forkedSession)
        {
            await LoadSessionsAsync();
            SyncSidebarSelection(forkedSession.Id);
        }

        private void OnSettingsSaved(object? sender, EventArgs e)
        {
            _providerCatalogController?.Invalidate();
            _groupedCatalogs?.Invalidate();
            _ = ReloadProviderSelectionAsync();
        }

        private async Task ReloadProviderSelectionAsync()
        {
            if (_apiService is null) return;
            try
            {
                var settings = await _apiService.GetSettingsAsync();
                if (settings is null) return;
                string backend = settings.BackendChoice ?? "openai";
                string model = Models.ProviderModelSelection.GetSelectedModel(settings, backend);
                _groupedCatalogs?.SetSelection(backend, model);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Reload provider selection failed: {ex.Message}");
            }
        }

        private bool _isSettingsModalOpen;

        private void OnSettingsModalOverlaySizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                SettingsModalCard.Width = Math.Min(880, Math.Max(400, e.NewSize.Width - 48));
                SettingsModalCard.Height = Math.Min(560, Math.Max(360, e.NewSize.Height - 40));
            }
        }

        private void OpenSettingsPopup()
        {
            _isSettingsModalOpen = true;
            SettingsModalOverlay.Visibility = Visibility.Visible;
            if (SettingsModalOverlay.ActualWidth > 0 && SettingsModalOverlay.ActualHeight > 0)
            {
                SettingsModalCard.Width = Math.Min(880, Math.Max(400, SettingsModalOverlay.ActualWidth - 48));
                SettingsModalCard.Height = Math.Min(560, Math.Max(360, SettingsModalOverlay.ActualHeight - 40));
            }
            _settingsView?.SetCurrentTheme(_currentTheme);
            if (_chatView is not null)
            {
                _settingsView?.SetCurrentPreset(_chatView.CurrentAgentPreset);
            }
            _ = _settingsView?.RefreshProviderCatalogAsync();

            NavSettings.Background = (Brush)Application.Current.Resources["RuCanvasBrush"];
            NavSettings.Foreground = (Brush)Application.Current.Resources["RuInkBrush"];
            CloseSettingsButton?.Focus(FocusState.Programmatic);
        }

        /// <summary>
        /// Opens settings with the provider section already showing, for a transcript
        /// message whose fix is adding or replacing a provider key.
        /// </summary>
        private void OnProviderSettingsRequested(object? sender, EventArgs e)
        {
            OpenSettingsPopup();
            _settingsView?.ShowProviders();
        }

        private void CloseSettingsPopup()
        {
            if (!_isSettingsModalOpen) return;

            _isSettingsModalOpen = false;
            SettingsModalOverlay.Visibility = Visibility.Collapsed;

            if (_activeNavButton != NavSettings)
            {
                NavSettings.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                NavSettings.Foreground = (Brush)Application.Current.Resources["RuMutedBrush"];
            }
        }

        private void OnSettingsBackdropPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            CloseSettingsPopup();
        }

        private void OnSettingsCardPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void OnCloseSettingsButtonClick(object sender, RoutedEventArgs e)
        {
            CloseSettingsPopup();
        }

        private void OnNavClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag)
            {
                if (tag == "settings")
                {
                    if (_isSettingsModalOpen)
                    {
                        CloseSettingsPopup();
                    }
                    else
                    {
                        OpenSettingsPopup();
                    }
                    return;
                }

                CloseSettingsPopup();
                SetActiveNav(btn);

                var destination = Services.ShellRoutePolicy.Resolve(tag);
                ContentFrame.Content = ViewFor(destination);
                if (Services.ShellRoutePolicy.KeepsCatalogPolling(tag))
                {
                    _ = _chatView?.LoadChatModelsAsync();
                    _providerCatalogController?.SetPollingActive(true);
                    _groupedCatalogs?.SetPollingActive(true);
                    _ = _groupedCatalogs?.RefreshIfDueAsync();
                    _providerCatalogTimer?.Start();
                }
                else
                {
                    _providerCatalogController?.SetPollingActive(false);
                    _groupedCatalogs?.SetPollingActive(false);
                    _providerCatalogTimer?.Stop();
                }
                _diagnosticsView?.SetActive(Services.ShellRoutePolicy.IsDiagnosticsActive(tag));
                _logView?.SetActive(Services.ShellRoutePolicy.IsLogActive(tag));
            }
        }

        private object? ViewFor(Services.ShellDestination destination) => destination switch
        {
            Services.ShellDestination.Log => _logView,
            Services.ShellDestination.Diagnostics => _diagnosticsView,
            Services.ShellDestination.Policy => _policyControlPanel,
            Services.ShellDestination.Audit => _auditPanel,
            _ => _chatView,
        };

        private void OnToolsMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
            {
                return;
            }

            _providerCatalogController?.SetPollingActive(false);
            _groupedCatalogs?.SetPollingActive(false);
            _providerCatalogTimer?.Stop();

            ContentFrame.Content = ViewFor(Services.ShellRoutePolicy.Resolve(tag));
            _diagnosticsView?.SetActive(Services.ShellRoutePolicy.IsDiagnosticsActive(tag));
            _logView?.SetActive(Services.ShellRoutePolicy.IsLogActive(tag));
        }

        public void NavigateToDiagnostics(string? correlationId = null)
        {
            ContentFrame.Content = _diagnosticsView;
            _diagnosticsView?.SetActive(true);
            _logView?.SetActive(false);
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                _diagnosticsView?.FilterByCorrelation(correlationId);
            }
        }


        private void ScheduleReconnect()
        {
            if (_closed || _reconnecting || _composition.IsHarness) return;
            if (_reconnectTimer == null)
            {
                _reconnectTimer = new DispatcherTimer { Interval = Services.UiTiming.ReconnectPoll };
                _reconnectTimer.Tick += async (_, __) => await TryReconnectBackendAsync();
            }
            if (!_reconnectTimer.IsEnabled)
            {
                _reconnectTimer.Start();
            }
        }

        private async Task<bool> TryReconnectBackendAsync()
        {
            if (_closed || _reconnecting) return false;
            _reconnecting = true;
            try
            {
                string? projectRoot = _composition.IsHarness
                    ? _composition.HarnessWorkspacePath ?? string.Empty
                    : ResolveProjectRoot();
                if (projectRoot == null) return false;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                var backendSession = await _backendProvider.StartAsync(projectRoot, cts.Token);
                if (backendSession != null)
                {
                    _reconnectTimer?.Stop();
                    await OnBackendReconnectedAsync(backendSession, projectRoot);
                    return true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Reconnect attempt failed: {ex.Message}");
            }
            finally
            {
                _reconnecting = false;
            }
            return false;
        }

        private async Task OnBackendReconnectedAsync(BackendSession backendSession, string projectRoot)
        {
            var successBrush = (SolidColorBrush)Application.Current.Resources["AppSuccessBrush"];
            StatusText.Text = $"{_backendProvider.DisplayName} Backend ready (127.0.0.1:{backendSession.HttpBaseUri.Port})";
            StatusDot.Fill = successBrush;

            if (_apiService != null)
            {
                _apiService.SettingsSaved -= OnSettingsSaved;
                _apiService.Dispose();
            }
            _apiService = new ApiService(backendSession);
            _apiService.RequestFailed += (_, args) =>
            {
                var err = args.Error;
                System.Diagnostics.Debug.WriteLine($"[ApiService.RequestFailed] {err.Method} status={err.StatusCode} {err.ErrorType}: {err.Message}");
                GuiDiagnosticWriter.WriteApiError(err.Method, err.ErrorType, err.StatusCode);
            };
            _apiService.SettingsSaved += OnSettingsSaved;

            if (_providerCatalogController != null)
            {
                await _providerCatalogController.DisposeAsync();
            }
            if (_groupedCatalogs != null)
            {
                await _groupedCatalogs.DisposeAsync();
            }
            var reconnectCatalogService = new ProviderCatalogService(_apiService);
            _providerCatalogController = new ProviderCatalogController(reconnectCatalogService);
            _groupedCatalogs = new GroupedProviderCatalogController(reconnectCatalogService);
            _providerCatalogController.SetPollingActive(true);
            _groupedCatalogs.SetPollingActive(true);

            _healthStatusService = new HealthStatusService(
                _apiService,
                () => _backendProvider.Session != null);

            if (_agentController != null)
            {
                _agentController.Dispose();
            }
            _agentController = _composition.CreateAgentRunController(this.DispatcherQueue);

            _chatView?.ReinitializeBackend(_apiService, _agentController, _providerCatalogController, _groupedCatalogs);
            _settingsView?.Initialize(_apiService, _providerCatalogController);
            _logView?.Initialize(_apiService);
            _diagnosticsView?.Initialize(_apiService);

            _sessionsTimer?.Start();
            if (!_composition.IsHarness)
            {
            }
            _providerCatalogTimer?.Start();

            if (_sessionsRefreshController != null)
            {
                await _sessionsRefreshController.RefreshAsync();
            }
            if (_healthRefreshController != null)
            {
                await _healthRefreshController.RefreshAsync();
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
                _sessionsTimer?.Stop();
                _providerCatalogTimer?.Stop();

                var dangerBrush = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
                StatusText.Text = $"{_backendProvider.DisplayName} Backend stopped. Reconnecting...";
                StatusDot.Fill = dangerBrush;

                if (!_closed && !_composition.IsHarness)
                {
                    ScheduleReconnect();
                }
            });
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_closed) return;
            _closed = true;
            _reconnectTimer?.Stop();
            _reconnectTimer = null;
            _startup.Cancel();
            _sessionsTimer?.Stop();
            _providerCatalogTimer?.Stop();
            _sessionsRefreshController?.SetActive(false);
            _healthRefreshController?.SetActive(false);
            if (_apiService != null)
            {
                _apiService.SettingsSaved -= OnSettingsSaved;
            }
            // GUI owns Host lifetime: closing GUI should terminate the shared Host (and its Worker/Broker Job).
            // Shutdown POST runs first on a single chain, then disposal, so the
            // HttpClient cannot be disposed while the shutdown request is in flight.
            if (_apiService != null)
            {
                var shutdownService = _apiService;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var shutdownCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await shutdownService.ShutdownHostAsync(shutdownCts.Token);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MainWindow] Host shutdown failed: {ex.Message}");
                    }
                    await DisposeWindowResourcesAsync();
                });
            }
            else
            {
                _ = Task.Run(DisposeWindowResourcesAsync);
            }
        }

        private async Task DisposeWindowResourcesAsync()
        {
            try
            {
                await _startupTask;
                _startup.Dispose();
                if (_sessionsRefreshController != null) await _sessionsRefreshController.DisposeAsync();
                _sessionsRefreshController = null;
                if (_healthRefreshController != null) await _healthRefreshController.DisposeAsync();
                _healthRefreshController = null;
                if (_providerCatalogController != null)
                {
                    await _providerCatalogController.DisposeAsync();
                    _providerCatalogController = null;
                }
                if (_groupedCatalogs != null)
                {
                    await _groupedCatalogs.DisposeAsync();
                    _groupedCatalogs = null;
                }
                if (_chatView != null)
                {
                    _chatView.SessionOpened -= OnChatSessionOpened;
                    _chatView.SessionForked -= OnChatSessionForked;
                    _chatView.Dispose();
                }
                _settingsView?.Dispose();
                _logView?.Dispose();
                _diagnosticsView?.Dispose();
                _apiService?.Dispose();
                _healthStatusService = null;
                _backendProvider.BackendExited -= OnBackendExited;
                _agentController?.Dispose();
                _agentController = null;
                _policyController?.Dispose();
                _policyController = null;
                if (_policyControlPanel != null)
                {
                    _policyControlPanel.Controller = null;
                    _policyControlPanel = null;
                }
                if (_auditPanel != null)
                {
                    _auditPanel.Log = null;
                    _auditPanel = null;
                }
                if (ReferenceEquals(Instance, this))
                {
                    Instance = null;
                }
                _composition.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainWindow] Shutdown error: {ex.Message}");
            }
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
            // The sessions poll fires every few seconds; rebuilding the sidebar rows
            // on every poll visibly flickers group chrome, so only re-render when
            // the list (order, membership, or row content) actually changed.
            var before = Services.SidebarRowFingerprint.ForSessions(Sessions);
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
                    existing.LastAgentActivityAt = incoming.LastAgentActivityAt;
                    existing.ParentSessionId = incoming.ParentSessionId;
                    existing.ForkedFromMessageId = incoming.ForkedFromMessageId;
                    existing.WorkspaceId = incoming.WorkspaceId;
                    existing.WorkspaceLabel = incoming.WorkspaceLabel;
                    existing.WorkspacePath = incoming.WorkspacePath;
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
                else if (!string.IsNullOrEmpty(Sessions[i].WorkspacePath))
                {
                    _sessionWorkspaces.Remove(Sessions[i].Id);
                }
            }

            if (!string.Equals(
                Services.SidebarRowFingerprint.ForSessions(Sessions),
                before,
                StringComparison.Ordinal))
            {
                RenderWorkspaceSidebar();
            }
        }

        private void SyncSidebarSelection(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            _activeSessionId = sessionId;
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



        private async Task UpdateHealthStatusAsync()
        {
            if (_healthRefreshController != null)
            {
                await _healthRefreshController.RefreshAsync();
            }
        }

        private void ApplyHealthStatus(HealthStatusSnapshot snapshot)
        {

            var successBrush = (SolidColorBrush)Application.Current.Resources["AppSuccessBrush"];
            var warningBrush = (SolidColorBrush)Application.Current.Resources["AppWarningBrush"];
            var dangerBrush = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];

            // 1. .NET Backend status
            bool backendRunning = snapshot.BackendRunning;

            // 2. Worker status
            bool workerOk = snapshot.WorkerOk;
            string? workerError = snapshot.WorkerError;


            // Overall summary text
            if (!backendRunning)
            {
                StatusDot.Fill = dangerBrush;
                StatusText.Text = "Could not connect to .NET Backend!";
                if (!_closed && !_composition.IsHarness && (_reconnectTimer == null || !_reconnectTimer.IsEnabled))
                {
                    ScheduleReconnect();
                }
            }
            else
            {
                if (_reconnectTimer?.IsEnabled == true)
                {
                    _reconnectTimer.Stop();
                }

                if (!workerOk)
                {
                    StatusDot.Fill = warningBrush;
                    StatusText.Text = backendRunning && workerError != null
                        ? $"AI Worker error ({workerError})"
                        : "AI Worker starting or not ready yet";
                }
                else
                {
                    StatusDot.Fill = successBrush;
                    StatusText.Text = _composition.IsHarness
                        ? "Harness ready"
                        : "System ready";
                }
            }
        }

        private async void OnNewChatClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null || _chatView == null) return;
            var session = await _apiService.CreateSessionAsync();
            if (session != null)
            {
                await LoadSessionsAsync();

                var selectedPath = _selectedWorkspaceKey is null
                    ? null
                    : ResolveWorkspacePath(_selectedWorkspaceKey);
                if (selectedPath is not null)
                {
                    _sessionWorkspaces.Set(session.Id, selectedPath);
                }

                _chatView.SetRequestedWorkspace(selectedPath);

                var target = _selectedWorkspaceKey ?? ResolveNewChatWorkspace();
                if (target is not null)
                {
                    _workspaceGroups.SetExpanded(target, true);
                    RenderWorkspaceSidebar();
                }

                // Select Chat tab if not selected
                OnNavClick(NavChat, new RoutedEventArgs());

                // Tell ChatView to switch to this new session
                await _chatView.OpenSessionAsync(session.Id, session.Title);
            }
        }

        private string? ResolveNewChatWorkspace()
        {
            var active = _activeSessionId is null
                ? null
                : Sessions.FirstOrDefault(s => s.Id == _activeSessionId);
            if (active is not null)
            {
                return WorkspaceSessionProjection.KeyFor(active);
            }

            var newest = Sessions.FirstOrDefault();
            return newest is null ? null : WorkspaceSessionProjection.KeyFor(newest);
        }

        private async void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressSidebarSelection)
            {
                return;
            }

            if (SidebarSessionList.SelectedItem is SessionInfo selected && _chatView != null)
            {
                _activeSessionId = selected.Id;

                if (!string.IsNullOrWhiteSpace(_workspaceSearchText))
                {
                    ClearWorkspaceSearch();
                    RenderWorkspaceSidebar();
                }

                var key = WorkspaceSessionProjection.KeyFor(selected);
                if (!_workspaceGroups.IsExpanded(key, defaultExpanded: true))
                {
                    _workspaceGroups.SetExpanded(key, true);
                    RenderWorkspaceSidebar();
                }

                _chatView.SetRequestedWorkspace(
                    !string.IsNullOrEmpty(selected.WorkspacePath)
                        ? selected.WorkspacePath
                        : _sessionWorkspaces.Lookup(selected.Id));

                // Select Chat tab if not selected
                OnNavClick(NavChat, new RoutedEventArgs());
                await _chatView.OpenSessionAsync(selected.Id, selected.Title);
                SidebarSessionList.ScrollIntoView(selected);
            }
            else if (SidebarSessionList.SelectedItem is WorkspaceGroupRow groupRow)
            {
                if (_selectedWorkspaceKey == groupRow.Group.Key)
                {
                    _selectedWorkspaceKey = null;
                    _suppressSidebarSelection = true;
                    try
                    {
                        SidebarSessionList.SelectedItem = null;
                    }
                    finally
                    {
                        _suppressSidebarSelection = false;
                    }
                }
                else
                {
                    _selectedWorkspaceKey = groupRow.Group.Key;
                }
            }
            else if (SidebarSessionList.SelectedItem is not null)
            {
                _suppressSidebarSelection = true;
                try
                {
                    SidebarSessionList.SelectedItem = null;
                }
                finally
                {
                    _suppressSidebarSelection = false;
                }
            }
        }

        private string _workspaceSearchText = string.Empty;
        private bool _workspaceFilterActive;
        private string? _activeSessionId;
        private string? _selectedWorkspaceKey;
        private bool _suppressSidebarSelection;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _workspaceSearchTimer;
        private const int WorkspaceGroupPageSize = 5;
        private readonly ObservableCollection<object> _sidebarRows = new();
        private readonly List<WorkspaceGroup> _lastWorkspaceGroups = new();
        private readonly WorkspaceGroupPreferenceStore _workspaceGroups = new();
        private readonly WorkspacePinStore _workspacePins = new();
        private readonly SessionWorkspaceMapStore _sessionWorkspaces = new();
        private readonly HashSet<string> _groupShowAll = new(StringComparer.Ordinal);

        private static string PathStateKey(string canonicalPath) =>
            "path:" + canonicalPath.ToLowerInvariant();

        private void RenderWorkspaceSidebar()
        {
            var sessions = Sessions.ToList();
            var activeKey = ActiveWorkspaceKey();
            if (_workspaceFilterActive && activeKey is not null)
            {
                sessions = sessions
                    .Where(session => WorkspaceSessionProjection.KeyFor(session) == activeKey)
                    .ToList();
            }

            var projection = WorkspaceSessionProjection.Project(sessions, _workspaceSearchText);
            var searching = !string.IsNullOrWhiteSpace(_workspaceSearchText);
            var groups = WorkspaceSessionProjection.MergePinned(
                projection.Groups,
                _workspacePins.PinnedPaths);
            if (searching)
            {
                groups = groups.Where(group => group.Sessions.Count > 0).ToList();
            }

            _suppressSidebarSelection = true;
            try
            {
                _sidebarRows.Clear();
                _lastWorkspaceGroups.Clear();
                _lastWorkspaceGroups.AddRange(groups);
                var first = true;
                foreach (var group in groups)
                {
                    var expanded = searching
                        || _workspaceGroups.IsExpanded(
                            group.Key,
                            defaultExpanded: activeKey is null ? first : group.Key == activeKey)
                        || (!string.IsNullOrEmpty(group.Path)
                            && _workspaceGroups.IsExpanded(PathStateKey(group.Path), defaultExpanded: false));
                    first = false;
                    _sidebarRows.Add(new WorkspaceGroupRow(group, expanded));
                    if (!expanded)
                    {
                        continue;
                    }

                    if (group.Sessions.Count > WorkspaceGroupPageSize
                        && !_groupShowAll.Contains(group.Key))
                    {
                        foreach (var session in group.Sessions.Take(WorkspaceGroupPageSize))
                        {
                            _sidebarRows.Add(session);
                        }

                        _sidebarRows.Add(new WorkspaceShowMoreRow(
                            group.Key,
                            $"Show {group.Sessions.Count - WorkspaceGroupPageSize} more"));
                    }
                    else
                    {
                        foreach (var session in group.Sessions)
                        {
                            _sidebarRows.Add(session);
                        }
                    }
                }

                if (_activeSessionId is not null)
                {
                    SyncSidebarSelection(_activeSessionId);
                }
            }
            finally
            {
                _suppressSidebarSelection = false;
            }

            var matchCount = _sidebarRows.OfType<SessionInfo>().Count();
            WorkspaceSearchCount.Text = matchCount == 0
                ? "No matches"
                : matchCount == 1 ? "1 result" : $"{matchCount} results";
            WorkspaceSearchCount.Visibility = searching ? Visibility.Visible : Visibility.Collapsed;
        }

        private string? ActiveWorkspaceKey()
        {
            if (_activeSessionId is null)
            {
                return null;
            }

            var active = Sessions.FirstOrDefault(session => session.Id == _activeSessionId);
            return active is null ? null : WorkspaceSessionProjection.KeyFor(active);
        }

        private void OnWorkspaceSearchClick(object sender, RoutedEventArgs e)
        {
            WorkspaceSearchBox.Visibility = Visibility.Visible;
            WorkspaceSearchBox.Focus(FocusState.Programmatic);
        }

        private void OnWorkspaceSearchTextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox box)
            {
                _workspaceSearchText = box.Text;
            }

            _workspaceSearchTimer ??= DispatcherQueue.CreateTimer();
            _workspaceSearchTimer.Interval = TimeSpan.FromMilliseconds(250);
            _workspaceSearchTimer.Tick -= OnWorkspaceSearchTick;
            _workspaceSearchTimer.Tick += OnWorkspaceSearchTick;
            _workspaceSearchTimer.Stop();
            _workspaceSearchTimer.Start();
        }

        private void OnWorkspaceSearchTick(Microsoft.UI.Dispatching.DispatcherQueueTimer timer, object args)
        {
            timer.Stop();
            RenderWorkspaceSidebar();
        }

        private void OnWorkspaceSearchKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                ClearWorkspaceSearch();
                RenderWorkspaceSidebar();
            }
            else if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var first = _sidebarRows.OfType<SessionInfo>().FirstOrDefault();
                if (first is not null)
                {
                    SidebarSessionList.SelectedItem = first;
                }
            }
        }

        private void ClearWorkspaceSearch()
        {
            _workspaceSearchText = string.Empty;
            WorkspaceSearchBox.Text = string.Empty;
            WorkspaceSearchBox.Visibility = Visibility.Collapsed;
        }

        private void OnWorkspaceFilterClick(object sender, RoutedEventArgs e)
        {
            _workspaceFilterActive = !_workspaceFilterActive;
            WorkspaceFilterButton.Background = _workspaceFilterActive
                ? (Brush)Application.Current.Resources["RuAccentSoftBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            RenderWorkspaceSidebar();
        }

        private void OnWorkspaceGroupClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string key)
            {
                // Flip the row's effective state (not the stored default) and keep the
                // group-key and path-key flags in sync; otherwise a pinned path flag
                // left true by OnWorkspaceAddClick pins the group open forever.
                var expanded = !(button.CommandParameter as bool?
                    ?? _workspaceGroups.IsExpanded(key, defaultExpanded: true));
                _workspaceGroups.SetExpanded(key, expanded);
                var path = ResolveWorkspacePath(key);
                if (!string.IsNullOrEmpty(path))
                {
                    _workspaceGroups.SetExpanded(PathStateKey(path), expanded);
                }

                RenderWorkspaceSidebar();
            }
        }

        private void OnWorkspaceShowMoreClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string key)
            {
                _groupShowAll.Add(key);
                RenderWorkspaceSidebar();
            }
        }

        /// <summary>Removes a pinned workspace; its folder and sessions stay untouched.</summary>
        private void OnWorkspaceRemoveClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.Tag is not string key) return;
            var path = ResolveWorkspacePath(key);
            if (path is null) return;

            _workspacePins.Unpin(path);
            if (_selectedWorkspaceKey == key)
            {
                _selectedWorkspaceKey = null;
            }

            RenderWorkspaceSidebar();
        }

        private async void OnWorkspaceGroupAddClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null || _chatView == null) return;
            if (sender is not Button button || button.Tag is not string key) return;
            var path = ResolveWorkspacePath(key);
            if (path is null) return;

            var session = await _apiService.CreateSessionAsync();
            if (session is null) return;
            _sessionWorkspaces.Set(session.Id, path);
            _chatView.SetRequestedWorkspace(path);
            await LoadSessionsAsync();

            _activeSessionId = session.Id;
            _selectedWorkspaceKey = key;
            _workspaceGroups.SetExpanded(key, true);
            RenderWorkspaceSidebar();

            OnNavClick(NavChat, new RoutedEventArgs());
            await _chatView.OpenSessionAsync(session.Id, session.Title);
        }

        private async void OnWorkspaceAddClick(object sender, RoutedEventArgs e)
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            var canonical = WorkspacePinStore.Canonicalize(folder.Path);
            _workspacePins.Pin(canonical);
            _workspaceGroups.SetExpanded(PathStateKey(canonical), true);
            RenderWorkspaceSidebar();

            foreach (var group in _lastWorkspaceGroups)
            {
                if (!string.IsNullOrEmpty(group.Path)
                    && string.Equals(group.Path, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    _workspaceGroups.SetExpanded(group.Key, true);
                    RenderWorkspaceSidebar();
                    break;
                }
            }
        }

        private string? ResolveWorkspacePath(string key)
        {
            foreach (var group in _lastWorkspaceGroups)
            {
                if (group.Key == key)
                {
                    return group.Path;
                }

                if (!string.IsNullOrEmpty(group.Path) && PathStateKey(group.Path) == key)
                {
                    return group.Path;
                }
            }

            foreach (var pinned in _workspacePins.PinnedPaths)
            {
                if (PathStateKey(pinned) == key)
                {
                    return pinned;
                }
            }

            return null;
        }

        private void WorkspaceGroupToggle_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                // Hover fill comes from the ListViewItem container alone; the
                // row button paints nothing so there is never a double border.
                SetWorkspaceGroupChromeVisible(button, visible: true);
            }
        }

        private void WorkspaceGroupToggle_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button button)
            {
                SetWorkspaceGroupChromeVisible(button, visible: false);
            }
        }

        private static void SetWorkspaceGroupChromeVisible(Button button, bool visible)
        {
            if (button.FindName("WorkspaceGroupAddButton") is Button addButton)
            {
                addButton.Opacity = visible ? 1.0 : 0.55;
            }

            if (button.FindName("WorkspaceGroupChevron") is UIElement chevron)
            {
                chevron.Opacity = visible ? 1.0 : 0.55;
            }
        }

        private void OnShellPreviewKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Escape && _isSessionUsageOpen)
            {
                e.Handled = true;
                CloseSessionUsage();
                return;
            }

            if (e.Key == Windows.System.VirtualKey.Escape && _isSettingsModalOpen)
            {
                e.Handled = true;
                CloseSettingsPopup();
                return;
            }

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
                OnNavClick(NavSettings, new RoutedEventArgs());
            }
            else if (e.Key == Windows.System.VirtualKey.Left)
            {
                e.Handled = true;
                NudgeSidebarWidth(-40);
            }
            else if (e.Key == Windows.System.VirtualKey.Right)
            {
                e.Handled = true;
                NudgeSidebarWidth(40);
            }
        }

        private void NudgeSidebarWidth(double delta)
        {
            if (_responsiveTier == ResponsiveLayoutTier.Narrow) return;

            double newWidth = SidebarColumn.ActualWidth + delta;
            if (newWidth <= 150)
            {
                SidebarColumn.MinWidth = 72;
                SidebarColumn.Width = new GridLength(72);
                ApplySidebarVisualMode(true);
            }
            else
            {
                SidebarColumn.MinWidth = 72;
                var clamped = Math.Clamp(newWidth, 180, 400);
                SidebarColumn.Width = new GridLength(clamped);
                _lastExpandedSidebarWidth = clamped;
                ApplySidebarVisualMode(false);
            }
        }

        private async void OnForkSessionMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.Tag is string sessionId && _apiService != null)
            {
                var targetSession = Sessions.FirstOrDefault(s => s.Id == sessionId);
                var branchTitle = targetSession != null ? $"{targetSession.Title} (Branch)" : "New Branch";
                var forked = await _apiService.ForkSessionAsync(sessionId, int.MaxValue, branchTitle);
                if (forked != null)
                {
                    await LoadSessionsAsync();
                    var newSession = Sessions.FirstOrDefault(s => s.Id == forked.Id);
                    if (newSession != null)
                    {
                        SidebarSessionList.SelectedItem = newSession;
                        if (_chatView != null)
                        {
                            await _chatView.OpenSessionAsync(newSession.Id, newSession.Title);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Hands a session to another coding agent, in that agent's own format and
        /// directory.
        /// </summary>
        /// <remarks>
        /// The two menu items differ only by the format they name, so the harness is read
        /// off the clicked item rather than duplicated into a second handler that would
        /// drift from this one.
        /// </remarks>
        private async void OnExportSessionMenuClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement item
                || item.Tag is not string sessionId
                || string.IsNullOrWhiteSpace(sessionId)
                || _apiService is null)
            {
                return;
            }

            var harness = item.Name switch
            {
                "ExportCodexItem" => "codex",
                _ => "claude-code",
            };

            var outcome = await _apiService.ExportSessionAsync(sessionId, harness);
            if (ShellRoot.XamlRoot is null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = outcome.Ok ? "Session exported" : "Could not export this session",
                Content = outcome.Ok
                    ? string.Join('\n', outcome.Paths) + "\n\nRestart that agent so it picks the session up."
                    : outcome.Error ?? "The export failed.",
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = ShellRoot.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        /// <summary>
        /// Brings another agent's session file in as a new Ru session.
        /// </summary>
        /// <remarks>
        /// The file chooses its own format: the name says which agent wrote it. When the
        /// name says nothing, this asks instead of guessing — a guessed format would read
        /// the file as the wrong thing and then refuse it, which reads like the file's
        /// fault.
        /// </remarks>
        private async void OnImportSessionMenuClick(object sender, RoutedEventArgs e)
        {
            if (_apiService is null || ShellRoot.XamlRoot is null)
            {
                return;
            }

            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add(".jsonl");
            WinRT.Interop.InitializeWithWindow.Initialize(
                picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));

            Windows.Storage.StorageFile? file = await picker.PickSingleFileAsync();
            if (file is null)
            {
                return;
            }

            var harness = SessionFileLayout.HarnessFromFileName(file.Path);
            if (harness is null)
            {
                await ShowNoticeAsync(
                    "Cannot tell which agent wrote this file",
                    $"Ru recognises {SessionFileLayout.ClaudeCodeId} transcripts, named after their session id, and {SessionFileLayout.CodexId} rollouts, named rollout-<when>-<thread>.");
                return;
            }

            var content = await Windows.Storage.FileIO.ReadTextAsync(file);
            var outcome = await _apiService.ImportSessionAsync(harness, content);
            if (!outcome.Ok)
            {
                await ShowNoticeAsync("Could not import this session", outcome.Error ?? "The import failed.");
                return;
            }

            await LoadSessionsAsync();

            await ShowNoticeAsync(
                "Session imported",
                outcome.DroppedEntries > 0
                    ? $"{outcome.Messages} messages came across as a new session. {outcome.DroppedEntries} entries stayed behind: Ru's log holds turns, and another agent's tool traffic belongs to runs."
                    : $"{outcome.Messages} messages came across as a new session.");
        }

        private async Task ShowNoticeAsync(string title, string content)
        {
            if (ShellRoot.XamlRoot is null)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = "Close",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = ShellRoot.XamlRoot,
            };
            await dialog.ShowAsync();
        }

        private async void OnDeleteSessionClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement btn && btn.Tag is string sessionId && _apiService != null)
            {
                if (ShellRoot.XamlRoot != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "Confirm delete chat",
                        Content = "Are you sure you want to permanently delete this chat?",
                        PrimaryButtonText = "Delete",
                        CloseButtonText = "Cancel",
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
                            Title = "Could not delete chat",
                            Content = "Backend failed to delete this chat. Please try again later.",
                            CloseButtonText = "Close",
                            DefaultButton = ContentDialogButton.Close,
                            XamlRoot = ShellRoot.XamlRoot
                        };
                        await errorDialog.ShowAsync();
                    }

                    return;
                }

                bool wasActive = string.Equals(_chatView?.CurrentSessionId, sessionId, StringComparison.OrdinalIgnoreCase);

                await LoadSessionsAsync();

                // If the deleted session was currently active, switch to another session or create a new one
                if (wasActive)
                {
                    if (Sessions.Count > 0)
                    {
                        var first = Sessions[0];
                        SidebarSessionList.SelectedItem = first;
                        if (_chatView != null)
                        {
                            await _chatView.OpenSessionAsync(first.Id, first.Title);
                        }
                    }
                    else
                    {
                        OnNewChatClick(this, new RoutedEventArgs());
                    }
                }
                else if (Sessions.Count == 0)
                {
                    OnNewChatClick(this, new RoutedEventArgs());
                }
            }
        }

        private void SessionItem_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            SetSessionRowActionsVisible(sender, visible: true);
        }

        private void SessionItem_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            SetSessionRowActionsVisible(sender, visible: false);
        }

        /// <summary>
        /// Reveals the row's actions in place of the clock at the right edge, and
        /// restores the clock on leave.
        /// </summary>
        /// <remarks>
        /// The clock is collapsed rather than dimmed: it is wider than the two 22px
        /// controls together, so leaving it visible would paint the time underneath
        /// them. Everything is right-aligned in one cell, so swapping them does not
        /// move anything else in the row.
        ///
        /// Hit testing is toggled with the opacity. Controls that stay invisible but
        /// still accept clicks would turn a stray click on the timestamp into a delete,
        /// or into a network read. Keyboard reach is deliberately not granted here —
        /// the row's context menu owns that path — so neither control is a tab stop.
        /// </remarks>
        private static void SetSessionRowActionsVisible(object sender, bool visible)
        {
            if (sender is not Grid grid)
            {
                return;
            }

            var cell = grid.Children.OfType<Grid>().FirstOrDefault();
            if (cell is null)
            {
                return;
            }

            if (cell.FindName("SessionDeleteButton") is Button deleteButton)
            {
                deleteButton.Opacity = visible ? 1.0 : 0.0;
                deleteButton.IsHitTestVisible = visible;
            }

            if (cell.FindName("SessionUsageButton") is Button usageButton)
            {
                usageButton.Opacity = visible ? 1.0 : 0.0;
                usageButton.IsHitTestVisible = visible;
            }

            if (cell.FindName("SessionTimeText") is TextBlock clock)
            {
                clock.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        /// <summary>
        /// Opens one session's token report, centred over the shell.
        ///
        /// The report used to be a flyout pinned to the row, which is where it was
        /// asked for and the worst place to read it: 268px wide beside a list, with
        /// three figures per run that wanted tabular space. It reads as a panel, so it
        /// opens as one.
        /// </summary>
        private async void OnSessionUsageClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button
                || button.Tag is not string sessionId
                || string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            await OpenSessionUsageAsync(sessionId, (button.DataContext as SessionInfo)?.Title);
        }

        private bool _isSessionUsageOpen;

        private async Task OpenSessionUsageAsync(string sessionId, string? sessionTitle)
        {
            if (_apiService is null)
            {
                return;
            }

            _isSessionUsageOpen = true;
            SessionUsageOverlay.Visibility = Visibility.Visible;
            ApplySessionUsageCardSize();
            CloseSessionUsageButton?.Focus(FocusState.Programmatic);

            await SessionUsagePanelControl.LoadAsync(_apiService, sessionId, sessionTitle);
        }

        private void CloseSessionUsage()
        {
            if (!_isSessionUsageOpen)
            {
                return;
            }

            _isSessionUsageOpen = false;
            SessionUsageOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnSessionUsageOverlaySizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                ApplySessionUsageCardSize();
            }
        }

        /// <summary>
        /// A fixed card keeps the panel from jittering as rows arrive; it shrinks with
        /// the window instead of overflowing it, with a floor below which the rows
        /// would have no room to render.
        /// </summary>
        private void ApplySessionUsageCardSize()
        {
            if (SessionUsageOverlay.ActualWidth <= 0 || SessionUsageOverlay.ActualHeight <= 0)
            {
                return;
            }

            SessionUsageCard.Width = Math.Min(560, Math.Max(360, SessionUsageOverlay.ActualWidth - 48));
            SessionUsageCard.Height = Math.Min(560, Math.Max(320, SessionUsageOverlay.ActualHeight - 40));
        }

        private void OnCloseSessionUsageClick(object sender, RoutedEventArgs e) => CloseSessionUsage();

        private void OnSessionUsageBackdropPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) =>
            CloseSessionUsage();

        private void OnSessionUsageCardPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Swallow the press so a click inside the card does not reach the backdrop
            // behind it and close the panel the reader is working in.
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

                SidebarColumn.MinWidth = 72;
                SidebarColumn.MaxWidth = metrics.MaxSidebarWidth;
                SidebarColumn.Width = new GridLength(metrics.NarrowRailWidth);
                ApplySidebarVisualMode(true);
            }
            else
            {
                SidebarColumn.MinWidth = 72;
                SidebarColumn.MaxWidth = metrics.MaxSidebarWidth;
                var restoredWidth = _responsiveTier == ResponsiveLayoutTier.Narrow
                    ? _lastExpandedSidebarWidth
                    : SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : metrics.DefaultSidebarWidth;
                var finalWidth = ResponsiveLayoutPolicy.ClampSidebarWidth(restoredWidth, metrics);
                SidebarColumn.Width = new GridLength(finalWidth);
                ApplySidebarVisualMode(finalWidth <= 120);
            }

            _responsiveTier = metrics.Tier;
        }

        private bool _isResizing = false;
        private double _startWidth = 0;
        private double _startX = 0;

        private void Resizer_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (SidebarResizerLine != null)
            {
                SidebarResizerLine.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuAccentBrush"];
                SidebarResizerLine.Opacity = Services.UiTiming.ResizerActiveOpacity;
                SidebarResizerLine.Width = 2;
            }
        }

        private void Resizer_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_isResizing && SidebarResizerLine != null)
            {
                SidebarResizerLine.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuLineBrush"];
                SidebarResizerLine.Opacity = 0.4;
                SidebarResizerLine.Width = 1;
            }
        }

        private void Resizer_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isResizing = true;
            _startX = e.GetCurrentPoint(null).Position.X;
            _startWidth = SidebarColumn.ActualWidth;
            if (sender is UIElement element)
            {
                element.CapturePointer(e.Pointer);
            }
            if (SidebarResizerLine != null)
            {
                SidebarResizerLine.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuAccentBrush"];
                SidebarResizerLine.Opacity = 1.0;
                SidebarResizerLine.Width = 2;
            }
        }

        private void Resizer_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isResizing)
            {
                double currentX = e.GetCurrentPoint(null).Position.X;
                double diff = currentX - _startX;
                double newWidth = _startWidth + diff;

                if (newWidth <= 150)
                {
                    SidebarColumn.MinWidth = 72;
                    SidebarColumn.Width = new GridLength(72);
                    ApplySidebarVisualMode(true);
                }
                else
                {
                    SidebarColumn.MinWidth = 72;
                    var clamped = Math.Clamp(newWidth, 180, 400);
                    SidebarColumn.Width = new GridLength(clamped);
                    _lastExpandedSidebarWidth = clamped;
                    ApplySidebarVisualMode(false);
                }
            }
        }

        private void Resizer_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isResizing = false;
            if (sender is UIElement element)
            {
                element.ReleasePointerCapture(e.Pointer);
            }
            if (SidebarResizerLine != null)
            {
                SidebarResizerLine.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuLineBrush"];
                SidebarResizerLine.Opacity = 0.4;
                SidebarResizerLine.Width = 1;
            }
        }

        private void Resizer_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            if (_responsiveTier == ResponsiveLayoutTier.Narrow) return;

            if (SidebarColumn.ActualWidth > 120)
            {
                _lastExpandedSidebarWidth = SidebarColumn.ActualWidth;
                SidebarColumn.MinWidth = 72;
                SidebarColumn.Width = new GridLength(72);
                ApplySidebarVisualMode(true);
            }
            else
            {
                var targetWidth = _lastExpandedSidebarWidth >= 180 ? _lastExpandedSidebarWidth : 250;
                SidebarColumn.MinWidth = 72;
                SidebarColumn.Width = new GridLength(targetWidth);
                ApplySidebarVisualMode(false);
            }
        }

        private void ApplySidebarVisualMode(bool isNarrow)
        {
            var labelVis = isNarrow ? Visibility.Collapsed : Visibility.Visible;
            if (LabelNewChat != null) LabelNewChat.Visibility = labelVis;
            if (LabelChat != null) LabelChat.Visibility = labelVis;
            if (LabelThemeToggle != null) LabelThemeToggle.Visibility = labelVis;
            if (LabelSettings != null) LabelSettings.Visibility = labelVis;
            if (WorkspaceHeaderPanel != null) WorkspaceHeaderPanel.Visibility = labelVis;
            if (StatusText != null) StatusText.Visibility = labelVis;
            if (SidebarSessionList != null) SidebarSessionList.Visibility = labelVis;

            if (isNarrow)
            {
                // Compact Mini Rail Mode (72px centered icon buttons)
                if (SidebarTopPanel != null)
                {
                    SidebarTopPanel.Padding = new Thickness(0, 16, 0, 0);
                    SidebarTopPanel.HorizontalAlignment = HorizontalAlignment.Center;
                }
                if (SidebarBottomPanel != null)
                {
                    SidebarBottomPanel.Padding = new Thickness(0, 8, 0, 14);
                    SidebarBottomPanel.HorizontalAlignment = HorizontalAlignment.Center;
                }

                ConfigureMiniButton(NavNewChat, ContentNewChat, IconNewChat);
                ConfigureMiniButton(NavChat, ContentChat, IconChat);
                ConfigureMiniButton(ThemeToggleButton, ContentThemeToggle, ThemeToggleIcon);
                ConfigureMiniButton(NavSettings, ContentSettings, IconSettings);

                if (ShellStatusRow != null)
                {
                    ShellStatusRow.Padding = new Thickness(0, 8, 0, 8);
                    ShellStatusRow.HorizontalAlignment = HorizontalAlignment.Center;
                    ShellStatusRow.Spacing = 0;
                }
            }
            else
            {
                // Full Expanded Mode
                if (SidebarTopPanel != null)
                {
                    SidebarTopPanel.Padding = new Thickness(14, 16, 14, 0);
                    SidebarTopPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                }
                if (SidebarBottomPanel != null)
                {
                    SidebarBottomPanel.Padding = new Thickness(14, 8, 14, 14);
                    SidebarBottomPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
                }

                ConfigureFullButton(NavNewChat, ContentNewChat, IconNewChat, new Thickness(0, 0, 10, 0));
                ConfigureFullButton(NavChat, ContentChat, IconChat, new Thickness(0, 0, 12, 0));
                ConfigureFullButton(ThemeToggleButton, ContentThemeToggle, ThemeToggleIcon, new Thickness(0, 0, 12, 0));
                ConfigureFullButton(NavSettings, ContentSettings, IconSettings, new Thickness(0, 0, 12, 0));

                if (ShellStatusRow != null)
                {
                    ShellStatusRow.Padding = new Thickness(10, 8, 10, 12);
                    ShellStatusRow.HorizontalAlignment = HorizontalAlignment.Left;
                    ShellStatusRow.Spacing = 8;
                }
            }
        }

        private static void ConfigureMiniButton(Button? btn, StackPanel? content, FontIcon? icon)
        {
            if (btn == null) return;
            btn.Width = 42;
            btn.Height = 42;
            btn.Padding = new Thickness(0);
            btn.HorizontalAlignment = HorizontalAlignment.Center;
            btn.HorizontalContentAlignment = HorizontalAlignment.Center;
            btn.VerticalContentAlignment = VerticalAlignment.Center;
            btn.CornerRadius = new CornerRadius(8);
            if (content != null)
            {
                content.HorizontalAlignment = HorizontalAlignment.Center;
                content.VerticalAlignment = VerticalAlignment.Center;
            }
            if (icon != null)
            {
                icon.Margin = new Thickness(0);
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                icon.VerticalAlignment = VerticalAlignment.Center;
            }
        }

        private static void ConfigureFullButton(Button? btn, StackPanel? content, FontIcon? icon, Thickness iconMargin)
        {
            if (btn == null) return;
            btn.Width = double.NaN;
            btn.Height = double.NaN;
            btn.Padding = new Thickness(10, 8, 10, 8);
            btn.HorizontalAlignment = HorizontalAlignment.Stretch;
            btn.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            btn.VerticalContentAlignment = VerticalAlignment.Center;
            btn.CornerRadius = new CornerRadius(6);
            if (content != null)
            {
                content.HorizontalAlignment = HorizontalAlignment.Left;
                content.VerticalAlignment = VerticalAlignment.Center;
            }
            if (icon != null)
            {
                icon.Margin = iconMargin;
                icon.HorizontalAlignment = HorizontalAlignment.Left;
                icon.VerticalAlignment = VerticalAlignment.Center;
            }
        }
    }
}
