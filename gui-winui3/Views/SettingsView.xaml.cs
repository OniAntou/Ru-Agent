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
                CustomEndpointUrlBox.Text = settings.CustomUrl;
                CustomApiKeyBox.Password = string.Empty;
                CustomApiKeyBox.PlaceholderText = settings.HasCustomKey
                    ? "Saved (leave blank to keep unchanged)"
                    : "sk-...";


                {
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
            CustomEndpointUrlBox.TextChanged -= AutoSaveTextHandler;
            CustomApiKeyBox.PasswordChanged -= AutoSaveHandler;
            if (_autoSaveTimer != null)
            {
                _autoSaveTimer.Tick -= OnAutoSaveTimerTick;
                _autoSaveTimer.Stop();
                _autoSaveTimer = null;
            }
            {
            }
            {
            }
        }

        private void AutoSaveHandler(object sender, RoutedEventArgs e) => ScheduleAutoSave();

        private void AutoSaveTextHandler(object sender, TextChangedEventArgs e) => ScheduleAutoSave();

        private string _activeSettingsTab = "system";

        private void InitializeSettingsHoverEffects()
        {
            SetupSettingsNavHover(NavSystemBtn, NavSystemIcon, NavSystemText, () => _activeSettingsTab == "system");
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

        private void SetActiveSettingsTab(string tab)
        {
            _activeSettingsTab = tab;
            var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            NavSystemBtn.Background = transparent;

            var isSystem = tab == "system";

            SwitchPanel(SystemSettingsScrollViewer, isSystem);

            UpdateNavButton(NavSystemIcon, NavSystemText, NavSystemLine, isSystem);
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
