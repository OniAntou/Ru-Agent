using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RuApp.Models;
using RuApp.Services;

namespace RuApp.Views
{
    public sealed partial class SettingsView : UserControl, IDisposable
    {
        private ApiService? _apiService;
        private ProviderCatalogController? _providerCatalogController;
        private bool _ownsProviderCatalogController;
        private bool _loading = false;
        private DispatcherTimer? _autoSaveTimer;
        private bool _saveInProgress;
        private bool _saveRequestedDuringSave;
        private DispatcherTimer? _vtsConfigDebounce;
        private bool _loadingVtsConfig;
        private DispatcherTimer? _vtsControlDebounce;
        private bool _syncingVtsControls;
        private enum PendingVtsControl { None, Overlay, MouseLook, Sensitivity }
        private PendingVtsControl _pendingVtsControl;

        public SettingsView()
        {
            this.InitializeComponent();
            InitializeSettingsHoverEffects();
        }

        public void Initialize(ApiService apiService, ProviderCatalogController? providerCatalogController = null)
        {
            DisposeEventHandlers();
            _apiService = apiService;
            _providerCatalogController = providerCatalogController ?? new ProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsProviderCatalogController = providerCatalogController is null;
            _ = LoadSettingsAsync();
            SetupAutoSave();
            SetupVts();
        }

        private void SetupVts()
        {
            _vtsConfigDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _vtsConfigDebounce.Tick += OnVtsConfigDebounceTick;

            _vtsControlDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _vtsControlDebounce.Tick += OnVtsControlDebounceTick;

            _ = LoadVtsConfigAsync();
            _ = PollVtsStatusAsync();
        }

        private async void OnVtsConfigDebounceTick(object? sender, object e)
        {
            if (_vtsConfigDebounce != null)
            {
                _vtsConfigDebounce.Stop();
            }
            await SaveVtsConfigAsync();
            await LoadVtsConfigAsync();
        }

        private async void OnVtsControlDebounceTick(object? sender, object e)
        {
            if (_vtsControlDebounce != null)
            {
                _vtsControlDebounce.Stop();
            }
            await FlushPendingVtsControlAsync();
        }

        private void SetupAutoSave()
        {
            _autoSaveTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };
            _autoSaveTimer.Tick += OnAutoSaveTimerTick;

            BackendComboBox.SelectionChanged += AutoSaveHandler;
            OpenCodeApiKeyBox.PasswordChanged += AutoSaveHandler;
            KiloApiKeyBox.PasswordChanged += AutoSaveHandler;
            OpenAiApiKeyBox.PasswordChanged += AutoSaveHandler;
            AnthropicApiKeyBox.PasswordChanged += AutoSaveHandler;
            GeminiVisionKeyBox.PasswordChanged += AutoSaveHandler;
            CustomEndpointUrlBox.TextChanged += AutoSaveTextHandler;
            CustomApiKeyBox.PasswordChanged += AutoSaveHandler;
        }

        private async void OnAutoSaveTimerTick(object? sender, object e)
        {
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Stop();
            }
            await SaveSettingsAsync();
        }

        private void ScheduleAutoSave()
        {
            if (_loading) return;
            AutoSaveStatusText.Text = "Saving...";
            _autoSaveTimer?.Stop();
            _autoSaveTimer?.Start();
        }

        private async Task LoadSettingsAsync()
        {
            _loading = true;
            if (_apiService != null)
            {
                var settings = await _apiService.GetSettingsAsync();
                settings ??= new AppSettingsDto();
                foreach (ComboBoxItem item in BackendComboBox.Items)
                {
                    if ((item.Tag as string) == settings.BackendChoice)
                    {
                        BackendComboBox.SelectedItem = item;
                        break;
                    }
                }
                if (BackendComboBox.SelectedItem == null && BackendComboBox.Items.Count > 0)
                {
                    BackendComboBox.SelectedIndex = 0;
                }

                OpenCodeApiKeyBox.Password = string.Empty;
                OpenCodeApiKeyBox.PlaceholderText = settings.HasOpenCodeKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-opencode-...";
                KiloApiKeyBox.Password = string.Empty;
                KiloApiKeyBox.PlaceholderText = settings.HasKiloKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-kilo-...";
                OpenAiApiKeyBox.Password = string.Empty;
                OpenAiApiKeyBox.PlaceholderText = settings.HasOpenAiKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-...";
                AnthropicApiKeyBox.Password = string.Empty;
                AnthropicApiKeyBox.PlaceholderText = settings.HasAnthropicKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-ant-...";
                GeminiVisionKeyBox.Password = string.Empty;
                GeminiVisionKeyBox.PlaceholderText = settings.HasGeminiVisionKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "AIza...";
                CustomEndpointUrlBox.Text = settings.CustomUrl;
                CustomApiKeyBox.Password = string.Empty;
                CustomApiKeyBox.PlaceholderText = settings.HasCustomKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-...";


                var obsStatus = await _apiService.GetObservationStatusAsync();
                if (obsStatus != null)
                {
                    ScreenObserverToggle.IsOn = obsStatus.Active;
                    ScreenObserverStatusText.Text = obsStatus.Active
                        ? "Status: Active (Every 1s · Real-time)"
                        : "Status: Disabled";
                }

                UpdateVisibilityForBackend(settings.BackendChoice);
                _providerCatalogController?.SetBackend(settings.BackendChoice);
            }
            _loading = false;
        }

        private void UpdateVisibilityForBackend(string choice)
        {
            OpenCodeApiRow.Visibility = (choice == "opencode") ? Visibility.Visible : Visibility.Collapsed;
            KiloApiRow.Visibility = (choice == "kilo") ? Visibility.Visible : Visibility.Collapsed;
            OpenAiApiRow.Visibility = (choice == "openai") ? Visibility.Visible : Visibility.Collapsed;
            AnthropicApiRow.Visibility = (choice == "anthropic") ? Visibility.Visible : Visibility.Collapsed;
            CustomEndpointRow.Visibility = (choice == "custom") ? Visibility.Visible : Visibility.Collapsed;
            CustomApiKeyRow.Visibility = (choice == "custom") ? Visibility.Visible : Visibility.Collapsed;

        }

        private void OnBackendSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || BackendComboBox.SelectedItem == null) return;
            string choice = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "opencode";

            UpdateVisibilityForBackend(choice);
            _providerCatalogController?.SetBackend(choice);
            _ = _providerCatalogController?.RefreshAsync(force: true);
        }

        public async Task RefreshProviderCatalogAsync()
        {
            if (_providerCatalogController is not null)
            {
                await _providerCatalogController.RefreshAsync(force: true);
            }
        }

        private async Task SaveSettingsAsync()
        {
            if (_apiService == null) return;
            if (_saveInProgress)
            {
                _saveRequestedDuringSave = true;
                return;
            }
            _saveInProgress = true;

            try
            {
                string backend = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "opencode";

                var dto = new ProviderSettingsUpdateDto
                {
                    BackendChoice = backend,
                    OpenCodeApiKey = OpenCodeApiKeyBox.Password,
                    KiloApiKey = KiloApiKeyBox.Password,
                    OpenAiApiKey = OpenAiApiKeyBox.Password,
                    AnthropicApiKey = AnthropicApiKeyBox.Password,
                    GeminiVisionApiKey = GeminiVisionKeyBox.Password,
                    CustomUrl = CustomEndpointUrlBox.Text?.Trim() ?? "http://localhost:8000/v1",
                    CustomApiKey = CustomApiKeyBox.Password,
                };

                bool ok = await _apiService.SaveProviderSettingsAsync(dto);
                AutoSaveStatusText.Text = ok ? "Saved" : "Save failed";
                if (!ok)
                {
                    System.Diagnostics.Debug.WriteLine("[Settings] Auto-save failed");
                }
            }
            catch (Exception ex)
            {
                AutoSaveStatusText.Text = "Save error";
                System.Diagnostics.Debug.WriteLine($"[Settings] Auto-save error: {ex.Message}");
            }
            finally
            {
                _saveInProgress = false;
                if (_saveRequestedDuringSave)
                {
                    _saveRequestedDuringSave = false;
                    _autoSaveTimer?.Stop();
                    _autoSaveTimer?.Start();
                }
            }
        }

        private async void OnRemoveProviderClick(object sender, RoutedEventArgs e)
        {
            if (_apiService is null)
            {
                return;
            }

            if (sender is not Button button
                || button.Tag is not string alias
                || string.IsNullOrWhiteSpace(alias))
            {
                return;
            }

            button.IsEnabled = false;
            try
            {
                bool ok = await _apiService.RemoveProviderAsync(alias);
                if (ok)
                {
                    ClearProviderKeyInput(alias);
                    await LoadSettingsAsync();
                }

                AutoSaveStatusText.Text = ok ? "Provider signed out" : "Sign out failed";
            }
            catch (Exception ex)
            {
                AutoSaveStatusText.Text = "Sign out error";
                System.Diagnostics.Debug.WriteLine($"[Settings] Sign out error: {ex.Message}");
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private void ClearProviderKeyInput(string alias)
        {
            switch (alias)
            {
                case "opencode":
                    OpenCodeApiKeyBox.Password = string.Empty;
                    break;
                case "kilo":
                    KiloApiKeyBox.Password = string.Empty;
                    break;
                case "openai":
                    OpenAiApiKeyBox.Password = string.Empty;
                    break;
                case "anthropic":
                    AnthropicApiKeyBox.Password = string.Empty;
                    break;
                case "custom":
                    CustomApiKeyBox.Password = string.Empty;
                    break;
            }
        }

        public void Dispose()
        {
            DisposeEventHandlers();
            if (_providerCatalogController is not null)
            {
                if (_ownsProviderCatalogController)
                {
                    _ = _providerCatalogController.DisposeAsync().AsTask();
                }
                _providerCatalogController = null;
                _ownsProviderCatalogController = false;
            }
            _apiService = null;
        }

        private void DisposeEventHandlers()
        {
            BackendComboBox.SelectionChanged -= AutoSaveHandler;
            OpenCodeApiKeyBox.PasswordChanged -= AutoSaveHandler;
            KiloApiKeyBox.PasswordChanged -= AutoSaveHandler;
            OpenAiApiKeyBox.PasswordChanged -= AutoSaveHandler;
            AnthropicApiKeyBox.PasswordChanged -= AutoSaveHandler;
            GeminiVisionKeyBox.PasswordChanged -= AutoSaveHandler;
            CustomEndpointUrlBox.TextChanged -= AutoSaveTextHandler;
            CustomApiKeyBox.PasswordChanged -= AutoSaveHandler;
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Tick -= OnAutoSaveTimerTick;
                _autoSaveTimer.Stop();
                _autoSaveTimer = null;
            }
            if (_vtsConfigDebounce != null)
            {
                _vtsConfigDebounce.Tick -= OnVtsConfigDebounceTick;
                _vtsConfigDebounce.Stop();
                _vtsConfigDebounce = null;
            }
            if (_vtsControlDebounce != null)
            {
                _vtsControlDebounce.Tick -= OnVtsControlDebounceTick;
                _vtsControlDebounce.Stop();
                _vtsControlDebounce = null;
            }
        }

        private void AutoSaveHandler(object sender, RoutedEventArgs e) => ScheduleAutoSave();

        private void AutoSaveTextHandler(object sender, TextChangedEventArgs e) => ScheduleAutoSave();

        private async void OnConnectVtsClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;
            ConnectVtsButton.IsEnabled = false;
            try
            {
                await _apiService.ConnectVtsAsync();
            }
            finally
            {
                ConnectVtsButton.IsEnabled = true;
            }
            _ = PollVtsStatusAsync();
        }

        private void OnVtsConfigSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"[SettingsView] Slider changed: {sender?.GetType().Name} new={e.NewValue:0}");
            if (_loadingVtsConfig) return;
            _vtsConfigDebounce?.Stop();
            _vtsConfigDebounce?.Start();
        }

        private async Task LoadVtsConfigAsync()
        {
            if (_apiService == null) return;
            _loadingVtsConfig = true;
            var config = await _apiService.GetVtsConfigAsync();
            if (config != null)
            {
                SwaySlider.Value = SwayAmplitudeToPercent(config.SwayAmplitudeIdle);
                HeadSlider.Value = config.HeadScale;
                EyeSlider.Value = config.EyeScale;
                MicroSlider.Value = MicroDelayToPercent(config.MicroActionMinDelay);
                PoseSlider.Value = PoseIntervalToPercent(config.PoseMinInterval);
            }
            _loadingVtsConfig = false;
        }

        private async Task SaveVtsConfigAsync()
        {
            if (_apiService == null) return;
            var config = new VtsConfigDto
            {
                SwayAmplitudeIdle = SwayPercentToAmplitude(SwaySlider.Value, idle: true),
                SwayAmplitudeSpeaking = SwayPercentToAmplitude(SwaySlider.Value, idle: false),
                HeadScale = HeadSlider.Value,
                EyeScale = EyeSlider.Value,
                MicroActionMinDelay = MicroPercentToDelay(MicroSlider.Value, min: true),
                MicroActionMaxDelay = MicroPercentToDelay(MicroSlider.Value, min: false),
                PoseMinInterval = PosePercentToInterval(PoseSlider.Value, min: true),
                PoseMaxInterval = PosePercentToInterval(PoseSlider.Value, min: false),
                BreathAmplitude = 1.5,
                BreathRate = 0.15,
            };
            System.Diagnostics.Debug.WriteLine($"[SettingsView] SaveVtsConfigAsync: sway={config.SwayAmplitudeIdle:0.0}/{config.SwayAmplitudeSpeaking:0.0} head={config.HeadScale:0.0} eye={config.EyeScale:0.0}");
            bool ok = await _apiService.SaveVtsConfigAsync(config);
            System.Diagnostics.Debug.WriteLine($"[SettingsView] SaveVtsConfigAsync result={ok}");
        }

        private static double SwayPercentToAmplitude(double percent, bool idle)
        {
            double min = idle ? 5.0 : 5.0;
            double max = idle ? 30.0 : 40.0;
            return min + (max - min) * percent / 100.0;
        }

        private static double SwayAmplitudeToPercent(double amplitude)
        {
            if (amplitude <= 5.0) return 0.0;
            if (amplitude >= 30.0) return 100.0;
            return (amplitude - 5.0) / 25.0 * 100.0;
        }

        private static double MicroPercentToDelay(double percent, bool min)
        {
            double high = min ? 40.0 : 90.0;
            double low = min ? 8.0 : 22.0;
            return low + (high - low) * (100.0 - percent) / 100.0;
        }

        private static double MicroDelayToPercent(double delay)
        {
            if (delay <= 8.0) return 100.0;
            if (delay >= 40.0) return 0.0;
            return 100.0 - (delay - 8.0) / 32.0 * 100.0;
        }

        private static double PosePercentToInterval(double percent, bool min)
        {
            double high = min ? 300.0 : 600.0;
            double low = min ? 90.0 : 130.0;
            return low + (high - low) * (100.0 - percent) / 100.0;
        }

        private static double PoseIntervalToPercent(double interval)
        {
            if (interval <= 90.0) return 100.0;
            if (interval >= 300.0) return 0.0;
            return 100.0 - (interval - 90.0) / 210.0 * 100.0;
        }

        private async Task PollVtsStatusAsync()
        {
            if (_apiService == null) return;
            var status = await _apiService.GetVtsStatusAsync();
            if (status == null) return;
            _syncingVtsControls = true;
            try
            {
                if (status.Connected)
                {
                    VtsStatusText.Text = string.IsNullOrEmpty(status.ModelName)
                        ? "Status: Connected"
                        : $"Status: Connected · {status.ModelName}";
                    VtsStateText.Text = string.IsNullOrEmpty(status.State) ? "" : "Motion: " + status.State;
                }
                else
                {
                    VtsStatusText.Text = "Status: Disconnected";
                    VtsStateText.Text = string.IsNullOrEmpty(status.ConnectError) ? "" : status.ConnectError;
                }

                OverlayToggle.IsOn = status.OverlayEnabled;
                MouseLookToggle.IsEnabled = status.Connected;
                MouseLookToggle.IsOn = status.MouseLookEnabled;
                MouseSensitivitySlider.IsEnabled = status.MouseLookEnabled && status.Connected;
                MouseSensitivitySlider.Value = status.MouseLookSensitivity * 100;
                MouseSensitivityText.Text = $"{status.MouseLookSensitivity * 100:0}%";

                bool overlayBroken = status.OverlayEnabled && !status.OverlayRunning;
                VtsOverlayErrorText.Text = "Overlay is enabled but process is not running (missing RuDesktopOverlay.exe?).";
                VtsOverlayErrorText.Visibility = overlayBroken ? Visibility.Visible : Visibility.Collapsed;
            }
            finally
            {
                _syncingVtsControls = false;
            }
        }

        private void OnOverlayToggleChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingVtsControls) return;
            _pendingVtsControl = PendingVtsControl.Overlay;
            _vtsControlDebounce?.Stop();
            _vtsControlDebounce?.Start();
        }

        private void OnMouseLookToggleChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingVtsControls) return;
            MouseSensitivitySlider.IsEnabled = MouseLookToggle.IsOn;
            _pendingVtsControl = PendingVtsControl.MouseLook;
            _vtsControlDebounce?.Stop();
            _vtsControlDebounce?.Start();
        }

        private void OnMouseSensitivityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (MouseSensitivityText != null)
                MouseSensitivityText.Text = $"{e.NewValue:0}%";
            if (_syncingVtsControls || !MouseLookToggle.IsOn) return;
            _pendingVtsControl = PendingVtsControl.Sensitivity;
            _vtsControlDebounce?.Stop();
            _vtsControlDebounce?.Start();
        }

        private async Task FlushPendingVtsControlAsync()
        {
            if (_apiService == null || _pendingVtsControl == PendingVtsControl.None) return;
            var pending = _pendingVtsControl;
            _pendingVtsControl = PendingVtsControl.None;
            switch (pending)
            {
                case PendingVtsControl.Overlay:
                    await _apiService.SaveOverlayAsync(OverlayToggle.IsOn);
                    break;
                case PendingVtsControl.MouseLook:
                    await _apiService.SaveMouseLookAsync(MouseLookToggle.IsOn, MouseSensitivitySlider.Value / 100.0);
                    break;
                case PendingVtsControl.Sensitivity:
                    await _apiService.SaveMouseLookAsync(MouseLookToggle.IsOn, MouseSensitivitySlider.Value / 100.0);
                    break;
            }
        }

        private async void OnScreenObserverToggled(object sender, RoutedEventArgs e)
        {
            if (_loading || _apiService == null) return;
            bool isOn = ScreenObserverToggle.IsOn;
            ScreenObserverStatusText.Text = isOn
                ? "Starting..."
                : "Pausing...";

            if (isOn)
            {
                bool success = await _apiService.StartObservationAsync(1.0);
                ScreenObserverStatusText.Text = success
                    ? "Status: Active (Every 1s · Real-time)"
                    : "Status: Startup error (Check API Key)";
                if (!success)
                {
                    ScreenObserverToggle.IsOn = false;
                }
            }
            else
            {
                await _apiService.StopObservationAsync();
                ScreenObserverStatusText.Text = "Status: Disabled";
            }
        }

        private string _activeSettingsTab = "system";

        private void InitializeSettingsHoverEffects()
        {
            SetupSettingsNavHover(NavSystemBtn, NavSystemIcon, NavSystemText, () => _activeSettingsTab == "system");
            SetupSettingsNavHover(NavObserverBtn, NavObserverIcon, NavObserverText, () => _activeSettingsTab == "observer");
            SetupSettingsNavHover(NavVtsBtn, NavVtsIcon, NavVtsText, () => _activeSettingsTab == "vts");
        }

        private void SetupSettingsNavHover(Button btn, FontIcon? icon, TextBlock? text, Func<bool> isActive)
        {
            btn.PointerEntered += (s, e) =>
            {
                if (!isActive())
                {
                    btn.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuCanvasBrush"];
                    if (text != null) text.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuInkBrush"];
                    if (icon != null) icon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuInkBrush"];
                }
            };
            btn.PointerExited += (s, e) =>
            {
                if (!isActive())
                {
                    btn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    if (text != null) text.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"];
                    if (icon != null) icon.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"];
                }
            };
        }

        private void OnNavSystemClick(object sender, RoutedEventArgs e) => SetActiveSettingsTab("system");
        private void OnNavObserverClick(object sender, RoutedEventArgs e) => SetActiveSettingsTab("observer");
        private void OnNavVtsClick(object sender, RoutedEventArgs e) => SetActiveSettingsTab("vts");

        private void SetActiveSettingsTab(string tab)
        {
            _activeSettingsTab = tab;
            var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            NavSystemBtn.Background = transparent;
            NavObserverBtn.Background = transparent;
            NavVtsBtn.Background = transparent;

            var isSystem = tab == "system";
            var isObserver = tab == "observer";
            var isVts = tab == "vts";

            SwitchPanel(SystemSettingsScrollViewer, isSystem);
            SwitchPanel(ObservationScrollViewer, isObserver);
            SwitchPanel(VtsSettingsScrollViewer, isVts);

            UpdateNavButton(NavSystemIcon, NavSystemText, NavSystemLine, isSystem);
            UpdateNavButton(NavObserverIcon, NavObserverText, NavObserverLine, isObserver);
            UpdateNavButton(NavVtsIcon, NavVtsText, NavVtsLine, isVts);
        }

        private static void SwitchPanel(FrameworkElement panel, bool active)
        {
            if (panel == null) return;
            if (active)
            {
                panel.Visibility = Visibility.Visible;
                AnimatePanelEntrance(panel);
            }
            else
            {
                panel.Visibility = Visibility.Collapsed;
            }
        }

        private static void AnimatePanelEntrance(FrameworkElement element)
        {
            var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var fadeAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = 0.3,
                To = 1.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(200)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(fadeAnim, element);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(fadeAnim, "Opacity");
            sb.Children.Add(fadeAnim);
            sb.Begin();
        }

        private static void UpdateNavButton(FontIcon icon, TextBlock text, Border line, bool active)
        {
            if (line != null)
            {
                var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    To = active ? 1.0 : 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(active ? 180 : 100)),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, line);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
                sb.Children.Add(anim);
                sb.Begin();
            }

            if (icon != null)
                icon.Foreground = (Microsoft.UI.Xaml.Media.Brush)(active ? Application.Current.Resources["RuAccentBrush"] : Application.Current.Resources["RuMutedBrush"]);
            if (text != null)
            {
                text.Foreground = (Microsoft.UI.Xaml.Media.Brush)(active ? Application.Current.Resources["RuInkBrush"] : Application.Current.Resources["RuMutedBrush"]);
                text.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Medium;
            }
        }
    }
}
