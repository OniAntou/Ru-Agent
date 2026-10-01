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
                    _appWindow?.SetIcon(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "AppIcon.ico"));
                }
                catch (Exception ex)
                {
                    GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] TitleBar setup failed: {ex.Message}");
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
            ApplyTitleBarTheme();
        }

        private void ApplyTitleBarTheme()
        {
            if (_appWindow?.TitleBar is not { } titleBar)
            {
                return;
            }

            // The native title bar takes Colors, but they come from the theme
            // palette so the chrome cannot drift from the app theme.
            var foreground = ((SolidColorBrush)Application.Current.Resources["RuInkBrush"]).Color;
            var muted = ((SolidColorBrush)Application.Current.Resources["RuMutedBrush"]).Color;
            var hover = ((SolidColorBrush)Application.Current.Resources["RuAccentSoftBrush"]).Color;
            var pressed = ((SolidColorBrush)Application.Current.Resources["RuLineBrush"]).Color;

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
                    GuiDiagnosticWriter.AppendTraceLine($"[ApiService.RequestFailed] {err.Method} status={err.StatusCode} {err.ErrorType}: {err.Message}");
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
                            GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Health poll failed: {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Sessions poll failed: {ex.Message}");
                    }
                    finally
                    {
                        _sessionsPollInFlight = false;
                    }
                };
                _sessionsTimer.Start();

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
            var navCorner = UiCornerRadius.Control;

            foreach (var button in new[] { NavChat, NavSettings })
            {
                button.Background = transparent;
                button.Foreground = muted;
                button.BorderBrush = transparent;
                button.BorderThickness = new Thickness(0);
                button.CornerRadius = navCorner;
            }

            activeBtn.Background = surface;
            activeBtn.Foreground = ink;
            activeBtn.BorderBrush = line;
            activeBtn.BorderThickness = new Thickness(1);
            activeBtn.CornerRadius = navCorner;
        }

        private void OnChatSessionOpened(object? sender, SessionOpenedEventArgs e)
        {
            SyncSidebarSelection(e.SessionId);
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
                string backend = settings.BackendChoice ?? string.Empty;
                string model = Models.ProviderModelSelection.GetSelectedModel(settings, backend);
                _groupedCatalogs?.SetSelection(backend, model);
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Reload provider selection failed: {ex.Message}");
            }
        }

        private bool _isSettingsModalOpen;
        private object? _settingsModalPreviousFocus;

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
            _settingsModalPreviousFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement();
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

            if (_settingsModalPreviousFocus is Control previousFocus)
            {
                previousFocus.Focus(FocusState.Programmatic);
            }

            _settingsModalPreviousFocus = null;
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
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Reconnect attempt failed: {ex.Message}");
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
                GuiDiagnosticWriter.AppendTraceLine($"[ApiService.RequestFailed] {err.Method} status={err.StatusCode} {err.ErrorType}: {err.Message}");
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
            // GUI owns Host lifetime: closing GUI should terminate the shared Host (and its Broker Job).
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
                        GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Host shutdown failed: {ex.Message}");
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
            // The caller is a background task because the Host shutdown POST is
            // I/O, but the views below are XAML objects: touching them from this
            // thread throws RPC_E_WRONG_THREAD. Dispose them on the UI thread.
            await RunOnDispatcherAsync(DisposeWindowResourcesOnUiThread);
            await _startupTask;
            DisposeSafely("startup", _startup.Dispose);
            await DisposeSafelyAsync("sessions refresh", () => _sessionsRefreshController?.DisposeAsync());
            _sessionsRefreshController = null;
            await DisposeSafelyAsync("health refresh", () => _healthRefreshController?.DisposeAsync());
            _healthRefreshController = null;
            await DisposeSafelyAsync("provider catalog", () => _providerCatalogController?.DisposeAsync());
            _providerCatalogController = null;
            await DisposeSafelyAsync("grouped catalogs", () => _groupedCatalogs?.DisposeAsync());
            _groupedCatalogs = null;
            // The composition owns the Host process and the Broker Job, so
            // it is disposed last and unconditionally: a view that throws on the
            // way out must never leave those children running.
            DisposeSafely("composition", _composition.Dispose);
        }

        private void DisposeWindowResourcesOnUiThread()
        {
            if (_chatView != null)
            {
                _chatView.SessionOpened -= OnChatSessionOpened;
                DisposeSafely("chat view", _chatView.Dispose);
            }

            DisposeSafely("settings view", () => _settingsView?.Dispose());
            DisposeSafely("log view", () => _logView?.Dispose());
            DisposeSafely("diagnostics view", () => _diagnosticsView?.Dispose());
            DisposeSafely("api service", () => _apiService?.Dispose());
            _healthStatusService = null;
            _backendProvider.BackendExited -= OnBackendExited;
            DisposeSafely("agent controller", () => _agentController?.Dispose());
            _agentController = null;
            DisposeSafely("policy controller", () => _policyController?.Dispose());
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
        }

        private Task RunOnDispatcherAsync(Action action)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] UI teardown error: {ex.Message}");
                    }

                    completion.SetResult();
                }))
            {
                // The dispatcher is gone with the window: the remaining teardown
                // still has to run so the Host process is not orphaned.
                completion.SetResult();
            }

            return completion.Task;
        }

        private static void DisposeSafely(string name, Action dispose)
        {
            try
            {
                dispose();
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Disposing {name} failed: {ex.Message}");
            }
        }

        private static async Task DisposeSafelyAsync(string name, Func<ValueTask?> dispose)
        {
            try
            {
                var pending = dispose();
                if (pending.HasValue)
                {
                    await pending.Value.ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Disposing {name} failed: {ex.Message}");
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

            // 2. Backend status
            bool backendHealthy = snapshot.BackendHealthy;
            string? backendError = snapshot.BackendError;


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

                if (!backendHealthy)
                {
                    StatusDot.Fill = warningBrush;
                    StatusText.Text = backendRunning && backendError != null
                        ? $"Backend error ({backendError})"
                        : "Backend starting or not ready yet";
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
            try
            {
                var session = await _apiService.CreateSessionAsync();
                if (session is null)
                {
                    // A dead button with no explanation is indistinguishable from a
                    // broken one: say the session could not be created.
                    StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
                    StatusText.Text = "Could not create a new chat session";
                    return;
                }

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
            catch (Exception ex)
            {
                // async void: an unhandled exception here tears the process down.
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] New chat failed: {ex.Message}");
                StatusDot.Fill = (SolidColorBrush)Application.Current.Resources["AppDangerBrush"];
                StatusText.Text = "Could not create a new chat session";
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

        /// <summary>
        /// Collapsed-rail nav item: the icon alone, centred in a square tap target.
        /// </summary>
        /// <remarks>
        /// Same corner as the expanded form. The two layouts are the same button at
        /// two widths, so a rail item that rounded differently read as a second style.
        /// </remarks>
        private static void ConfigureMiniButton(Button? btn, StackPanel? content, FontIcon? icon)
        {
            if (btn == null) return;
            btn.Width = 42;
            btn.Height = 42;
            btn.Padding = new Thickness(0);
            btn.HorizontalAlignment = HorizontalAlignment.Center;
            btn.HorizontalContentAlignment = HorizontalAlignment.Center;
            btn.VerticalContentAlignment = VerticalAlignment.Center;
            btn.CornerRadius = UiCornerRadius.Control;
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
            btn.CornerRadius = UiCornerRadius.Control;
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
