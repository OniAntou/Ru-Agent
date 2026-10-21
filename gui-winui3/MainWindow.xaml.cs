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
using RuApp.ViewModels;
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
        private AuditLogPanel? _auditPanel;
        private DispatcherTimer? _sessionsTimer;
        private ProviderCatalogController? _providerCatalogController;
        private GroupedProviderCatalogController? _groupedCatalogs;
        private DispatcherTimer? _providerCatalogTimer;
        private ResponsiveLayoutTier? _responsiveTier;
        private double _lastExpandedSidebarWidth = 250;
        private Microsoft.UI.Windowing.AppWindow? _appWindow;

        public static MainWindow? Instance { get; private set; }

        /// <summary>Last applied theme, so views can gate theme-bound decoration.</summary>
        internal static UiTheme CurrentTheme { get; private set; } = UiTheme.Light;

        /// <summary>The sidebar's sessions, groups and session workflows.</summary>
        internal SessionSidebarViewModel Sidebar { get; }

        /// <summary>Startup, Host status, navigation, Settings sync and title-bar state.</summary>
        internal ShellViewModel Shell { get; }

        public ObservableCollection<SessionInfo> Sessions => Sidebar.Sessions;

        public MainWindow()
            : this(RuAppComposition.Production())
        {
        }

        public MainWindow(RuAppComposition composition)
        {
            _composition = composition ?? throw new ArgumentNullException(nameof(composition));
            _backendProvider = composition.BackendProvider;
            Instance = this;
            Sidebar = new SessionSidebarViewModel(
                new ApiSessionListBackend(() => _apiService),
                action =>
                {
                    if (!DispatcherQueue.TryEnqueue(() => action()))
                    {
                        throw new OperationCanceledException("The window dispatcher rejected a sidebar callback.");
                    }
                });
            Sidebar.Rendered += OnSidebarRendered;
            Sidebar.PropertyChanged += OnSidebarPropertyChanged;
            Sidebar.ActionFailed += SetSessionActionError;
            Shell = new ShellViewModel(
                new HostShellBackend(
                    _backendProvider,
                    composition,
                    () => _apiService,
                    () => _healthStatusService,
                    () => _providerCatalogController,
                    () => _groupedCatalogs,
                    RebindBackendAsync),
                action =>
                {
                    if (!DispatcherQueue.TryEnqueue(() => action()))
                    {
                        throw new OperationCanceledException("The window dispatcher rejected a shell callback.");
                    }
                });
            Shell.PropertyChanged += OnShellPropertyChanged;
            Shell.ProjectRootResolved += OnProjectRootResolved;
            Shell.ChatPresetChosen += preset => _chatView?.SetAgentPreset(preset);
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

                ApplyTheme(Shell.Theme);
                InitializeSidebarHoverEffects();
                InitializeSidePanelToggle();

                this.Closed += OnWindowClosed;

                _startupTask = InitializeAppSafelyAsync();
            }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.WriteCrashEvent(ex);
                throw;
            }
        }

        private void OnThemeToggleClick(object sender, RoutedEventArgs e) => Shell.ToggleTheme();

        private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ShellViewModel.Theme):
                    ApplyTheme(Shell.Theme);
                    _settingsView?.SetCurrentTheme(Shell.Theme);
                    break;
                case nameof(ShellViewModel.SettingsPreset):
                    _settingsView?.SetCurrentPreset(Shell.SettingsPreset);
                    break;
                case nameof(ShellViewModel.StartupPhase):
                    ApplyWelcomeStatus();
                    break;
                case nameof(ShellViewModel.IsBackendLive):
                    ApplyBackendLiveness();
                    break;
            }
        }

        private void ApplyWelcomeStatus()
        {
            switch (Shell.StartupPhase)
            {
                case ShellStartupPhase.Starting:
                    WelcomeOverlay?.SetStatus(Shell.WelcomeText, isReady: false);
                    break;
                case ShellStartupPhase.RootMissing:
                    WelcomeOverlay?.SetStatus(Shell.WelcomeText, isReady: true, isError: true);
                    break;
                case ShellStartupPhase.Ready:
                    WelcomeOverlay?.NotifyReady();
                    break;
                case ShellStartupPhase.Failed:
                    WelcomeOverlay?.NotifyReady(isError: true);
                    break;
            }
        }

        /// <summary>The polls run only while a Host session is up.</summary>
        private void ApplyBackendLiveness()
        {
            if (!Shell.IsBackendLive)
            {
                _sessionsTimer?.Stop();
                _providerCatalogTimer?.Stop();
                return;
            }

            _sessionsTimer?.Start();
            if (Shell.Destination == ShellDestination.Chat)
            {
                _providerCatalogTimer?.Start();
            }
        }

        private Brush StatusBrush(ShellStatusTone tone) => (Brush)Application.Current.Resources[tone switch
        {
            ShellStatusTone.Ready => "AppSuccessBrush",
            ShellStatusTone.Danger => "AppDangerBrush",
            _ => "AppWarningBrush",
        }];

        private void OnProjectRootResolved(string projectRoot) =>
            Sidebar.SetAppRoot(AgentWorkspaceSelection.DefaultForApplication(projectRoot, _composition.IsHarness));

        private bool _hasAppliedTheme;

        private void ApplyTheme(UiTheme theme)
        {
            // The palette swap itself cannot be animated — ThemeResource bindings
            // only re-resolve on a theme change — so the shell fades through the
            // flip instead of blinking from one theme to the other. The first apply
            // is the startup theme and lands instantly, before the window is up.
            if (!_hasAppliedTheme)
            {
                _hasAppliedTheme = true;
                ApplyThemeCore(theme);
                return;
            }

            Services.UiMotion.SwapContent(ShellRoot, () => ApplyThemeCore(theme));
        }

        private void ApplyThemeCore(UiTheme theme)
        {
            CurrentTheme = theme;
            bool isDark = RuApp.Services.RuThemePalettes.IsDark(theme);
            var target = isDark ? ElementTheme.Dark : ElementTheme.Light;

            // The pack is written into the active base dictionary, so flip the
            // requested theme around the write: ThemeResource bindings only
            // re-resolve on a theme change, and Studio Dark and Starry Night
            // share the Dark base.
            if (ShellRoot.RequestedTheme == target)
            {
                ShellRoot.RequestedTheme = isDark ? ElementTheme.Light : ElementTheme.Dark;
            }
            RuApp.Services.RuThemePalettes.Apply(theme);
            ShellRoot.RequestedTheme = target;

            // The label changes with the palette inside the fade, so it is set
            // here rather than bound.
            LabelThemeToggle.Text = Shell.ThemeToggleLabel;
            ThemeToggleIcon.Glyph = Shell.ThemeToggleGlyph;
            AutomationProperties.SetName(ThemeToggleButton, Shell.ThemeToggleLabel);
            ApplyTitleBarTheme();
            _chatView?.RefreshBackdropEffect();
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

        private async Task InitializeAppSafelyAsync()
        {
            try { await InitializeAppAsync(); }
            catch (OperationCanceledException) when (_startup.Token.IsCancellationRequested) { }
            catch (Exception ex)
            {
                GuiDiagnosticWriter.WriteCrashEvent(ex);
                if (!_closed) Shell.ReportError("Could not start Ru. Please check the logs.");
            }
        }

        private async Task InitializeAppAsync()
        {
            SidebarSessionList.ItemsSource = Sidebar.Rows;
            InitializeSidebarMotion();

            // The shell publishes the project root before it starts the Host, so
            // section chrome and workspace pins render without waiting for it.
            BackendSession? backendSession = await _startup.RunAsync(token => Shell.StartAsync(token));
            if (backendSession != null)
            {
                _apiService = CreateApiService(backendSession);
                var catalogService = new ProviderCatalogService(_apiService);
                _providerCatalogController = new ProviderCatalogController(catalogService);
                _groupedCatalogs = new GroupedProviderCatalogController(catalogService);
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
                    Sidebar.AppRoot,
                    _providerCatalogController,
                    _groupedCatalogs);
                // The trust prompt names the run's folder from the Host's session record.
                _chatView.SessionWorkspaceResolver = sessionId =>
                    Sidebar.Sessions.FirstOrDefault(session => session.Id == sessionId)?.WorkspacePath;
                _chatView.SessionOpened += OnChatSessionOpened;
                _chatView.SessionStarted += OnChatSessionStarted;
                _chatView.SessionPresetAdopted += OnChatSessionPresetAdopted;
                // A failure message that offers to open provider settings needs the
                // window: the settings surface belongs to it, not to the transcript.
                _chatView.ProviderSettingsRequested += OnProviderSettingsRequested;

                _settingsView = new SettingsView();
                _settingsView.UpdateAvailabilityChanged += Shell.SetUpdateAvailable;
                _settingsView.CanInstallUpdate = () => _agentController?.IsBusy != true && _agentController?.CanCancel != true;
                _settingsView.UpdateRestartRequested += () => { _updateHandOff = true; Close(); };
                _settingsView.Initialize(_apiService, _providerCatalogController);
                _settingsView.SetCurrentTheme(Shell.Theme);
                Shell.ShowChatPreset(_chatView.CurrentAgentPreset);
                _settingsView.SetCurrentPreset(Shell.SettingsPreset);
                _settingsView.ThemeChanged += Shell.ChooseTheme;
                _settingsView.PresetChanged += Shell.ChoosePresetInSettings;
                SettingsModalContent.Content = _settingsView;
                Shell.MarkSettingsReady();
                _settingsView.StartUpdateAvailabilityChecks();

                _logView = new LogView();
                _logView.Initialize(_apiService);
                _logView.SetActive(false);

                _diagnosticsView = new DiagnosticsView();
                _diagnosticsView.Initialize(_apiService);
                _diagnosticsView.SetActive(false);

                _auditPanel = new AuditLogPanel { Log = _composition.AuditLog };

                // The conversation surface is Agent-only.
                ContentFrame.Content = _chatView;
                SetActiveNav(NavChat);
                _providerCatalogController.SetPollingActive(true);
                _groupedCatalogs?.SetPollingActive(true);
                _ = _groupedCatalogs?.RefreshIfDueAsync();
                _providerCatalogTimer.Start();

                Sidebar.SetRefreshActive(true);
                await _startup.RunAsync(_ => Sidebar.RefreshAsync());

                Shell.SetHealthActive(true);
                await _startup.RunAsync(_ => Shell.RefreshHealthAsync());

                _sessionsTimer = new DispatcherTimer { Interval = Services.UiTiming.SessionsPoll };
                _sessionsTimer.Tick += async (_, __) => await Shell.PollAsync(() => Sidebar.RefreshAsync());
                _sessionsTimer.Start();

                if (_composition.IsHarness && WelcomeOverlay != null && !WelcomeOverlay.IsDismissed)
                {
                    _ = WelcomeOverlay.DismissAsync();
                }
            }
        }

        private ApiService CreateApiService(BackendSession backendSession)
        {
            var api = new ApiService(backendSession);
            api.RequestFailed += (_, args) =>
            {
                var err = args.Error;
                GuiDiagnosticWriter.AppendTraceLine($"[ApiService.RequestFailed] {err.Method} status={err.StatusCode} {err.ErrorType}: {err.Message}");
                GuiDiagnosticWriter.WriteApiError(err.Method, err.ErrorType, err.StatusCode);
            };
            api.SettingsSaved += OnSettingsSaved;
            return api;
        }

        private Button? _activeNavButton;

        private void InitializeSidebarHoverEffects()
        {
            SetupNavHover(NavChat);
            SetupNavHover(NavSettings);

            // Theme toggle button
            ThemeToggleButton.PointerEntered += (s, e) =>
            {
                Services.UiMotion.FadeBrush(ThemeToggleButton, Control.BackgroundProperty, (Brush)Application.Current.Resources["RuCanvasBrush"]);
                Services.UiMotion.FadeBrush(ThemeToggleButton, Control.ForegroundProperty, (Brush)Application.Current.Resources["RuInkBrush"]);
            };
            ThemeToggleButton.PointerExited += (s, e) =>
            {
                Services.UiMotion.FadeBrush(ThemeToggleButton, Control.BackgroundProperty, null);
                Services.UiMotion.FadeBrush(ThemeToggleButton, Control.ForegroundProperty, (Brush)Application.Current.Resources["RuMutedBrush"]);
            };
        }

        private void SetupNavHover(Button btn)
        {
            btn.PointerEntered += (s, e) =>
            {
                if (btn != _activeNavButton && (btn != NavSettings || !Shell.IsSettingsOpen))
                {
                    Services.UiMotion.FadeBrush(btn, Control.BackgroundProperty, (Brush)Application.Current.Resources["RuCanvasBrush"]);
                    Services.UiMotion.FadeBrush(btn, Control.ForegroundProperty, (Brush)Application.Current.Resources["RuInkBrush"]);
                }
            };
            btn.PointerExited += (s, e) =>
            {
                if (btn != _activeNavButton && (btn != NavSettings || !Shell.IsSettingsOpen))
                {
                    Services.UiMotion.FadeBrush(btn, Control.BackgroundProperty, null);
                    Services.UiMotion.FadeBrush(btn, Control.ForegroundProperty, (Brush)Application.Current.Resources["RuMutedBrush"]);
                }
            };
        }

        private void SetActiveNav(Button activeBtn)
        {
            _activeNavButton = activeBtn;
            var muted = (SolidColorBrush)Application.Current.Resources["RuMutedBrush"];
            var ink = (SolidColorBrush)Application.Current.Resources["RuInkBrush"];
            var surface = (SolidColorBrush)Application.Current.Resources["RuSurfaceBrush"];
            var line = (SolidColorBrush)Application.Current.Resources["RuLineBrush"];
            var navCorner = UiCornerRadius.Control;

            foreach (var button in new[] { NavChat, NavSettings })
            {
                Services.UiMotion.FadeBrush(button, Control.BackgroundProperty, null);
                Services.UiMotion.FadeBrush(button, Control.ForegroundProperty, muted);
                Services.UiMotion.FadeBrush(button, Control.BorderBrushProperty, null);
                button.BorderThickness = new Thickness(0);
                button.CornerRadius = navCorner;
            }

            Services.UiMotion.FadeBrush(activeBtn, Control.BackgroundProperty, surface);
            Services.UiMotion.FadeBrush(activeBtn, Control.ForegroundProperty, ink);
            Services.UiMotion.FadeBrush(activeBtn, Control.BorderBrushProperty, line);
            activeBtn.BorderThickness = new Thickness(1);
            activeBtn.CornerRadius = navCorner;
        }

        private void OnChatSessionOpened(object? sender, SessionOpenedEventArgs e)
        {
            Sidebar.ActivateDrafts(e.SessionId);
            SyncSidebarSelection(e.SessionId);
        }

        private void OnChatSessionPresetAdopted(object? sender, SessionPresetAdoptedEventArgs e)
        {
            // The Settings dropdown shows the durable choice of the session that
            // is open, not the previous session's.
            Shell.AdoptSessionPreset(e.SessionId, e.Preset, Sidebar.ActiveSessionId);
        }

        private void OnSettingsSaved(object? sender, EventArgs e) =>
            _ = Shell.ReloadAfterSettingsSavedAsync();

        private object? _settingsModalPreviousFocus;

        private void OnSettingsModalOverlaySizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
            {
                SettingsModalCard.Width = Math.Max(0, Math.Min(880, e.NewSize.Width - 48));
                SettingsModalCard.Height = Math.Max(0, Math.Min(560, e.NewSize.Height - 40));
            }
        }

        private void OpenSettingsPopup()
        {
            if (Shell.OpenSettings(_chatView?.CurrentAgentPreset))
                _settingsModalPreviousFocus = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement();
            ShellInteractionHost.IsEnabled = false;
            SettingsModalOverlay.Visibility = Visibility.Visible;
            Services.UiMotion.Enter(SettingsModalOverlay);
            if (SettingsModalOverlay.ActualWidth > 0 && SettingsModalOverlay.ActualHeight > 0)
            {
                SettingsModalCard.Width = Math.Max(0, Math.Min(880, SettingsModalOverlay.ActualWidth - 48));
                SettingsModalCard.Height = Math.Max(0, Math.Min(560, SettingsModalOverlay.ActualHeight - 40));
            }
            _settingsView?.SetCurrentTheme(Shell.Theme);
            _settingsView?.SetCurrentPreset(Shell.SettingsPreset);
            _ = _settingsView?.RefreshProviderCatalogAsync();

            Services.UiMotion.FadeBrush(NavSettings, Control.BackgroundProperty, (Brush)Application.Current.Resources["RuCanvasBrush"]);
            Services.UiMotion.FadeBrush(NavSettings, Control.ForegroundProperty, (Brush)Application.Current.Resources["RuInkBrush"]);
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
            if (!Shell.CloseSettings()) return;

            _settingsView?.DismissFlyouts();
            ShellInteractionHost.IsEnabled = true;

            if (_activeNavButton != NavSettings)
            {
                Services.UiMotion.FadeBrush(NavSettings, Control.BackgroundProperty, null);
                Services.UiMotion.FadeBrush(NavSettings, Control.ForegroundProperty, (Brush)Application.Current.Resources["RuMutedBrush"]);
            }

            // The overlay leaves with a fade; the collapse and the focus hand-back
            // follow it, unless the modal was reopened while it faded.
            Services.UiMotion.Exit(SettingsModalOverlay, () =>
            {
                if (Shell.IsSettingsOpen)
                {
                    return;
                }

                SettingsModalOverlay.Visibility = Visibility.Collapsed;
                if (_settingsModalPreviousFocus is Control previousFocus)
                {
                    previousFocus.Focus(FocusState.Programmatic);
                }

                _settingsModalPreviousFocus = null;
            });
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
                    if (Shell.IsSettingsOpen)
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

                ApplyNavigation(Shell.Navigate(tag));
            }
        }

        private void ApplyNavigation(ShellNavigation navigation)
        {
            if (navigation.LeavesChat) LeaveChatSession();
            SwapNavContent(navigation.Destination);
            if (navigation.KeepsCatalogPolling)
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
            _diagnosticsView?.SetActive(navigation.IsDiagnosticsActive);
            _logView?.SetActive(navigation.IsLogActive);
        }

        private object? ViewFor(Services.ShellDestination destination) => destination switch
        {
            Services.ShellDestination.Log => _logView,
            Services.ShellDestination.Diagnostics => _diagnosticsView,
            Services.ShellDestination.Audit => _auditPanel,
            _ => _chatView,
        };

        private long _navSwapGeneration;

        /// <summary>
        /// Shows the destination view with a dip-fade, so switching screens reads as
        /// a change instead of a flash. A newer click during the fade wins: the
        /// superseded swap stands down and the newest click owns the content frame.
        /// </summary>
        private void SwapNavContent(Services.ShellDestination destination)
        {
            var content = ViewFor(destination);
            if (ReferenceEquals(ContentFrame.Content, content))
            {
                return;
            }

            long generation = ++_navSwapGeneration;
            Services.UiMotion.SwapContent(ContentFrame, () =>
            {
                if (generation != _navSwapGeneration)
                {
                    return;
                }

                ContentFrame.Content = content;
            });
        }

        private void OnToolsMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuFlyoutItem item || item.Tag is not string tag)
            {
                return;
            }

            ApplyNavigation(Shell.Navigate(tag));
        }

        public void NavigateToDiagnostics(string? correlationId = null)
        {
            ApplyNavigation(Shell.Navigate("diagnostics"));
            if (!string.IsNullOrWhiteSpace(correlationId))
            {
                _diagnosticsView?.FilterByCorrelation(correlationId);
            }
        }


        /// <summary>
        /// Rebuilds the Host-bound services and views on a reconnected session. The
        /// shell has already reported the Host ready and refreshes health afterwards.
        /// </summary>
        private async Task RebindBackendAsync(BackendSession backendSession)
        {
            if (_apiService != null)
            {
                _apiService.SettingsSaved -= OnSettingsSaved;
                _apiService.Dispose();
            }
            _apiService = CreateApiService(backendSession);

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

            await Sidebar.RefreshAsync();
        }

        private void OnWindowClosed(object sender, WindowEventArgs args)
        {
            if (_closed) return;
            _closed = true;
            StopSessionTitleWaves();
            Shell.Close();
            _startup.Cancel();
            _sessionsTimer?.Stop();
            _providerCatalogTimer?.Stop();
            Sidebar.SetRefreshActive(false);
            if (_apiService != null)
            {
                _apiService.SettingsSaved -= OnSettingsSaved;
            }
            // GUI owns Host lifetime: closing GUI should terminate the shared Host (and its Broker Job).
            // Shutdown POST runs first on a single chain, then disposal, so the
            // HttpClient cannot be disposed while the shutdown request is in flight.
            if (_apiService != null && !_updateHandOff)
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
            await DisposeSafelyAsync("sessions sidebar", () => Sidebar.DisposeAsync());
            await DisposeSafelyAsync("shell", () => Shell.DisposeAsync());
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
                ResetSidebarDrag();
                _chatView.SessionOpened -= OnChatSessionOpened;
                _chatView.SessionStarted -= OnChatSessionStarted;
                _chatView.SessionPresetAdopted -= OnChatSessionPresetAdopted;
                DisposeSafely("chat view", _chatView.Dispose);
            }

            DisposeSafely("settings view", () => _settingsView?.Dispose());
            DisposeSafely("log view", () => _logView?.Dispose());
            DisposeSafely("diagnostics view", () => _diagnosticsView?.Dispose());
            DisposeSafely("api service", () => _apiService?.Dispose());
            _healthStatusService = null;
            DisposeSafely("agent controller", () => _agentController?.Dispose());
            _agentController = null;

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

        private void SyncSidebarSelection(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            Sidebar.NoteOpened(sessionId);
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



        private async void OnNewChatClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null || _chatView == null) return;
            try
            {
                var (target, path) = Sidebar.NewChatTarget();
                await CreateDraftSessionAsync(target, path);
            }
            catch (Exception ex)
            {
                // async void: an unhandled exception here tears the process down.
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] New chat failed: {ex.Message}");
                Shell.ReportError("Could not create a new chat session");
            }
        }

        private async void OnSessionSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                await HandleSessionSelectionChangedAsync();
            }
            catch (Exception ex)
            {
                // async void: switching sessions awaits the API, and an unhandled
                // exception here would take the process down.
                GuiDiagnosticWriter.AppendTraceLine($"[MainWindow] Session switch failed: {ex.Message}");
                SetSessionActionError("Could not open the chat");
            }
        }

        private async Task HandleSessionSelectionChangedAsync()
        {
            if (_suppressSidebarSelection)
            {
                return;
            }

            if (SidebarSessionList.SelectedItem is SessionInfo selected && _chatView != null)
            {
                _chatView.SetRequestedWorkspace(Sidebar.Select(selected));

                // Select Chat tab if not selected
                OnNavClick(NavChat, new RoutedEventArgs());
                await _chatView.OpenSessionAsync(selected.Id, selected.Title);
                // The open awaited, and the user may have selected another row in
                // the meantime: scrolling must follow the row still selected, not
                // the one this pass started from.
                if (SidebarSessionList.SelectedItem is SessionInfo current
                    && string.Equals(current.Id, selected.Id, StringComparison.Ordinal))
                {
                    SidebarSessionList.ScrollIntoView(selected);
                }
            }
            else if (SidebarSessionList.SelectedItem is WorkspaceGroupRow groupRow)
            {
                if (!Sidebar.SelectGroup(groupRow.Group.Key))
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
