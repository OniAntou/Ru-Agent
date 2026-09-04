using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Dispatching;
using RuAgent.Client;
using RuAgent.Harness.Core;
using RuAgent.Protocol.Harness;
using RuApp.Models;
using RuApp.Services;
using Windows.Storage.Streams;

namespace RuApp.Views
{
    public sealed class SessionOpenedEventArgs : EventArgs
    {
        public SessionOpenedEventArgs(string sessionId, string title)
        {
            SessionId = sessionId;
            Title = title;
        }

        public string SessionId { get; }
        public string Title { get; }
    }

    public sealed class ChatMessageTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? UserTemplate { get; set; }
        public DataTemplate? AssistantTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is ChatMessageItem msg)
            {
                if (msg.Role == "user") return UserTemplate;
                return AssistantTemplate;
            }
            return base.SelectTemplateCore(item, container);
        }
    }

    public sealed partial class ChatView : UserControl, IDisposable
    {
        private const string VtsMarkerPattern = @"\s*\[\[(?:VTS|VOICE_PACE):[^\]]*\]\]";
        private const double ReadingColumnMaxWidth = 820;
        private const int MaxImageAttachmentBytes = Services.ChatAttachmentPolicy.MaxImageAttachmentBytes;
        private const int MaxTextAttachmentBytes = Services.ChatAttachmentPolicy.MaxTextAttachmentBytes;

        private ApiService? _apiService;
        private VoicePlaybackService? _voicePlayback;
        private VoiceTurnCaptureService? _voiceCapture;
        private VoiceConversationService? _voiceConversation;
        private HarnessEventSink? _harnessEvents;
        private string? _harnessScenario;
        private string _activeSessionId = "default";
        private AgentRunController? _agentController;
        private CancellationTokenSource? _agentMonitorCts;
        private readonly AgentSessionRecovery _agentSessionRecovery = new();
        private bool _agentActivityVisible;
        private string _agentWorkspace = Environment.CurrentDirectory;
        private string? _defaultAgentWorkspace;
        private bool _agentActionPending;
        private AgentRunSummaryItem? _agentRunSummary;
        public ObservableCollection<ChatMessageItem> Messages { get; } = new();
        public ObservableCollection<ImageAttachmentItem> PendingAttachments { get; } = new();
        public ObservableCollection<AgentActivityItem> AgentActivities { get; } = new();
        private readonly Dictionary<string, AgentActivityItem> _agentActivityById = new(StringComparer.Ordinal);
        private DispatcherTimer? _typingDebounce;
        private bool _typingPushed;
        private enum AgentTurnSource
        {
            Typed,
            Voice,
        }

        private AgentTurnSource _activeAgentTurnSource;
        private ChatMessageItem? _activeAgentAssistantMessage;
        private string? _activeAgentRunId;
        private string? _activeWorkspaceId;
        private bool _activeAgentReplyReceived;
        private bool _activeAgentReplyCompleted;
        private readonly HashSet<string> _agentDeltaKeys = new(StringComparer.Ordinal);
        private readonly AgentReplyAccumulator _replyAccumulator = new();
        private readonly AgentReplyRenderScheduler _replyRenderScheduler;

        public ChatView()
        {
            this.InitializeComponent();
            _replyRenderScheduler = new AgentReplyRenderScheduler(
                _replyAccumulator,
                callback =>
                {
                    if (!DispatcherQueue.TryEnqueue(() => callback()))
                    {
                        throw new OperationCanceledException("The chat dispatcher rejected a render callback.");
                    }
                },
                ApplyReplySnapshot);
            ChatListView.ItemsSource = Messages;
            AttachmentPreviewList.ItemsSource = PendingAttachments;
            AgentActivityFeed.ItemsSource = AgentActivities;
            Messages.CollectionChanged += (s, e) => UpdateViewMode();
            RootGrid.SizeChanged += OnRootGridSizeChanged;
            this.Loaded += (s, e) => ApplyInputWidth();
            InitializeChatHoverEffects();
            UpdateViewMode();
        }

        private void InitializeChatHoverEffects()
        {
            var canvas = (Brush)Application.Current.Resources["RuCanvasBrush"];
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var ink = (Brush)Application.Current.Resources["RuInkBrush"];
            var muted = (Brush)Application.Current.Resources["RuMutedBrush"];
            var accentSoft = (Brush)Application.Current.Resources["RuAccentSoftBrush"];
            var line = (Brush)Application.Current.Resources["RuLineBrush"];

            if (AttachImageButton != null)
            {
                AttachImageButton.PointerEntered += (s, e) =>
                {
                    AttachImageButton.Background = canvas;
                    if (AttachImageIcon != null) AttachImageIcon.Foreground = ink;
                };
                AttachImageButton.PointerExited += (s, e) =>
                {
                    AttachImageButton.Background = transparent;
                    if (AttachImageIcon != null) AttachImageIcon.Foreground = muted;
                };
            }

            if (MicButton != null)
            {
                MicButton.PointerEntered += (s, e) =>
                {
                    MicButton.Background = canvas;
                    if (MicIcon != null) MicIcon.Foreground = ink;
                };
                MicButton.PointerExited += (s, e) =>
                {
                    MicButton.Background = transparent;
                    if (MicIcon != null) MicIcon.Foreground = muted;
                };
            }

            if (SendButton != null)
            {
                SendButton.PointerEntered += (s, e) =>
                {
                    SendButton.Background = line;
                };
                SendButton.PointerExited += (s, e) =>
                {
                    SendButton.Background = accentSoft;
                };
            }

            if (ContextMeterButton != null)
            {
                ContextMeterButton.PointerEntered += (s, e) =>
                {
                    ContextMeterButton.Background = canvas;
                };
                ContextMeterButton.PointerExited += (s, e) =>
                {
                    ContextMeterButton.Background = transparent;
                };
            }
        }

        private void UpdateViewMode()
        {
            bool hasMessages = Messages.Count > 0;
            if (WelcomeHeroPanel != null)
            {
                WelcomeHeroPanel.Visibility = hasMessages ? Visibility.Collapsed : Visibility.Visible;
            }
            if (ChatScrollHost != null)
            {
                ChatScrollHost.Visibility = hasMessages ? Visibility.Visible : Visibility.Collapsed;
            }
            if (MeteorShowerField != null)
            {
                MeteorShowerField.IsActive = !hasMessages;
            }
        }

        private void OnRootGridSizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyInputWidth();
        }

        public event EventHandler<SessionOpenedEventArgs>? SessionOpened;

        private ProviderCatalogController? _providerCatalogController;
        private bool _ownsProviderCatalogController;
        private GroupedProviderCatalogController? _groupedCatalogs;
        private bool _ownsGroupedCatalogs;
        private string _selectedModel = string.Empty;
        private string _selectedReasoningEffort = "default";
        private DispatcherTimer? _modelSaveFailureTimer;

        public void Initialize(
            ApiService apiService,
            VoicePlaybackService voicePlayback,
            Func<IVoiceFrameSource>? voiceSourceFactory = null,
            HarnessEventSink? harnessEvents = null,
            string? harnessScenario = null,
            AgentRunController? agentController = null,
            string? defaultAgentWorkspace = null,
            ProviderCatalogController? providerCatalogController = null,
            GroupedProviderCatalogController? groupedProviderCatalogs = null)
        {
            _apiService = apiService;
            _voicePlayback = voicePlayback;
            _harnessEvents = harnessEvents;
            _harnessScenario = harnessScenario;
            _agentController = agentController;
            string defaultWorkspace = string.IsNullOrWhiteSpace(defaultAgentWorkspace)
                ? Environment.CurrentDirectory
                : defaultAgentWorkspace.Trim();
            _defaultAgentWorkspace = defaultWorkspace;
            _agentWorkspace = defaultWorkspace;
            _providerCatalogController = providerCatalogController ?? new ProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsProviderCatalogController = providerCatalogController == null;
            _groupedCatalogs = groupedProviderCatalogs ?? new GroupedProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsGroupedCatalogs = groupedProviderCatalogs == null;
            _groupedCatalogs.StateChanged += OnGroupedStateChanged;
            _agentController?.PropertyChanged += OnAgentControllerChanged;
            _agentController?.ActivityReceived += OnAgentActivityReceived;
            _agentController?.SummaryReceived += OnAgentSummaryReceived;
            _agentController?.AssistantDeltaReceived += OnAgentAssistantDeltaReceived;
            UpdateAgentComposerVisuals();
            UpdateAgentInlineState();
            _voiceCapture = new VoiceTurnCaptureService(voiceSourceFactory);
            _voiceConversation = new VoiceConversationService(
                _voiceCapture,
                TranscribeVoiceAsync,
                SendVoiceMessageAsync,
                harnessEvents);
            _voiceConversation.StatusChanged += OnVoiceConversationStatusChanged;

            ChatInputTextBox.TextChanged += OnChatInputTyping;
            _typingDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            _typingDebounce.Tick += (_, __) =>
            {
                _typingDebounce.Stop();
                if (!string.IsNullOrEmpty(ChatInputTextBox.Text) && !_typingPushed)
                {
                    _typingPushed = true;
                    _ = _apiService?.PushVtsActivityAsync(true);
                }
            };

            Messages.CollectionChanged += (_, __) => UpdateContextUsage();
            PendingAttachments.CollectionChanged += (_, __) => UpdateContextUsage();

            _ = LoadChatModelsAsync();
        }

        public async Task LoadChatModelsAsync()
        {
            if (_apiService == null) return;
            try
            {
                var settings = await _apiService.GetSettingsAsync();
                if (settings == null) return;
                string backend = settings.BackendChoice ?? "opencode";
                string currentModel = backend switch
                {
                    "opencode" => settings.OpenCodeModel ?? string.Empty,
                    "kilo" => settings.KiloModel ?? string.Empty,
                    "openai" => settings.OpenAiModel ?? string.Empty,
                    "anthropic" => settings.AnthropicModel ?? string.Empty,
                    "ollama" => settings.OllamaModel ?? string.Empty,
                    "lmstudio" => settings.LmStudioModel ?? string.Empty,
                    "custom" => settings.CustomModel ?? string.Empty,
                    _ => ""
                };

                _selectedModel = currentModel;
                _groupedCatalogs?.SetSelection(backend, currentModel);
                ChatModelLabel.Text = ProviderModelPickerProjection.TriggerDisplay(backend, currentModel);
                if (_providerCatalogController != null)
                {
                    _providerCatalogController.SetBackend(backend);
                    await _providerCatalogController.RefreshAsync();
                }
                if (_groupedCatalogs != null)
                {
                    await _groupedCatalogs.RefreshAsync(force: false);
                }
                UpdateReasoningEffortUi();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatView] Error loading models: {ex.Message}");
            }
        }

        private void OnChatModelFlyoutOpened(object sender, object e)
        {
            ModelPicker.FocusSearch();
            _ = RefreshGroupedCatalogsAsync(force: true);
        }

        private async Task RefreshGroupedCatalogsAsync(bool force)
        {
            if (_groupedCatalogs == null) return;
            await _groupedCatalogs.RefreshAsync(force);
        }

        private void OnModelPickerSearchTextChanged(object? sender, string searchText)
        {
            _groupedCatalogs?.SetSearchText(searchText);
        }

        private void OnModelPickerModelSelected(object? sender, ProviderModelSelectionEventArgs e)
        {
            ChatModelFlyout.Hide();
            _ = SelectChatModelAsync(e.Model, e.Backend);
        }

        private void OnGroupedStateChanged(object? sender, GroupedProviderCatalogState state)
        {
            if (!DispatcherQueue.TryEnqueue(() =>
            {
                if (_groupedCatalogs is null ||
                    !ReferenceEquals(_groupedCatalogs.State, state))
                {
                    return;
                }

                ApplyGroupedState(state);
            }))
            {
                return;
            }
        }

        private void ApplyGroupedState(GroupedProviderCatalogState state)
        {
            if (!string.IsNullOrWhiteSpace(state.SelectedModel))
            {
                _selectedModel = state.SelectedModel;
            }
            ChatModelLabel.Text = ProviderModelPickerProjection.TriggerDisplay(
                state.SelectedBackend, state.SelectedModel);
            ModelPicker.Apply(ProviderModelPickerProjection.Create(
                state.Catalogs.Values,
                state.SelectedBackend,
                state.SelectedModel,
                state.SearchText));
            UpdateContextUsage();
            UpdateReasoningEffortUi();
        }

        private async Task SelectChatModelAsync(string model, string backend)
        {
            try
            {
                if (_apiService is null ||
                    string.IsNullOrWhiteSpace(backend) ||
                    string.IsNullOrWhiteSpace(model))
                {
                    ShowModelSaveFailure();
                    return;
                }

                if (await _apiService.SaveProviderModelSelectionAsync(backend, model))
                {
                    _selectedModel = model;
                    _groupedCatalogs?.SetSelection(backend, model);
                    ChatModelLabel.Text = ProviderModelPickerProjection.TriggerDisplay(backend, model);
                    UpdateContextUsage();
                    if (_providerCatalogController != null)
                    {
                        _providerCatalogController.SetBackend(backend);
                        _ = _providerCatalogController.RefreshAsync();
                    }
                    UpdateReasoningEffortUi();
                }
                else
                {
                    ShowModelSaveFailure();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatView] Error saving model: {ex.Message}");
                ShowModelSaveFailure();
            }
        }

        private void UpdateReasoningEffortUi()
        {
            var options = GetCurrentReasoningOptions();

            if (!options.Any(o => string.Equals(o.Value, _selectedReasoningEffort, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedReasoningEffort = "default";
            }

            ChatReasoningLabel.Text = _selectedReasoningEffort;
            AutomationProperties.SetName(ChatReasoningButton, $"Mức độ suy luận: {_selectedReasoningEffort}");
        }

        private IReadOnlyList<ProviderReasoningOption> GetCurrentReasoningOptions()
        {
            if (_groupedCatalogs is null)
            {
                return [new ProviderReasoningOption("default", "default")];
            }

            var groupedState = _groupedCatalogs.State;
            if (string.IsNullOrWhiteSpace(groupedState.SelectedBackend))
            {
                return [new ProviderReasoningOption("default", "default")];
            }

            if (groupedState.Catalogs.TryGetValue(groupedState.SelectedBackend, out var catalogState))
            {
                if (!string.IsNullOrWhiteSpace(catalogState.SelectedEffort) &&
                    string.Equals(_selectedReasoningEffort, "default", StringComparison.OrdinalIgnoreCase))
                {
                    _selectedReasoningEffort = catalogState.SelectedEffort;
                }
                return ProviderReasoningOptions.For(catalogState);
            }

            return [new ProviderReasoningOption("default", "default")];
        }

        private void OnChatReasoningFlyoutOpened(object sender, object e)
        {
            ReasoningOptionsContainer.Children.Clear();
            var options = GetCurrentReasoningOptions();

            foreach (var option in options)
            {
                bool isSelected = string.Equals(option.Value, _selectedReasoningEffort, StringComparison.OrdinalIgnoreCase);

                var btn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Padding = new Thickness(10, 6, 10, 6),
                    CornerRadius = new CornerRadius(5),
                    MinHeight = 30
                };
                btn.Resources["ButtonBackgroundPointerOver"] = (Brush)Application.Current.Resources["RuAccentSoftBrush"];
                btn.Resources["ButtonBackgroundPressed"] = (Brush)Application.Current.Resources["RuAccentSoftBrush"];
                btn.Resources["ButtonBorderBrushPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                btn.Resources["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var titleBlock = new TextBlock
                {
                    Text = option.Label ?? option.Value,
                    FontFamily = (FontFamily)Application.Current.Resources["RuUiFontFamily"],
                    FontSize = 12,
                    Foreground = (Brush)Application.Current.Resources["RuInkBrush"],
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(titleBlock, 0);
                grid.Children.Add(titleBlock);

                if (isSelected)
                {
                    var checkIcon = new FontIcon
                    {
                        Glyph = "\uE73E",
                        FontSize = 12,
                        Foreground = (Brush)Application.Current.Resources["RuAccentBrush"],
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(16, 0, 2, 0)
                    };
                    Grid.SetColumn(checkIcon, 1);
                    grid.Children.Add(checkIcon);
                }

                btn.Content = grid;
                string effort = option.Value;
                btn.Click += async (s, args) =>
                {
                    ChatReasoningFlyout.Hide();
                    await ApplyReasoningEffortAsync(effort);
                };

                ReasoningOptionsContainer.Children.Add(btn);
            }
        }

        private async Task ApplyReasoningEffortAsync(string effort)
        {
            string previousEffort = _selectedReasoningEffort;
            _selectedReasoningEffort = effort;
            ChatReasoningLabel.Text = effort;
            AutomationProperties.SetName(ChatReasoningButton, $"Mức độ suy luận: {effort}");

            if (_apiService is null) return;

            string backend = _groupedCatalogs?.State.SelectedBackend ?? string.Empty;
            string model = _selectedModel ?? string.Empty;
            if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(backend)) return;

            string identity = backend;
            if (_groupedCatalogs != null &&
                _groupedCatalogs.State.Catalogs.TryGetValue(backend, out var catalogState) &&
                !string.IsNullOrWhiteSpace(catalogState.Identity))
            {
                identity = catalogState.Identity;
            }

            bool ok = await _apiService.SaveReasoningSettingsAsync(effort, model, identity);
            if (!ok)
            {
                _selectedReasoningEffort = previousEffort;
                ChatReasoningLabel.Text = previousEffort;
                AutomationProperties.SetName(ChatReasoningButton, $"Mức độ suy luận: {previousEffort} (Lưu thất bại)");
            }
        }

        private void ShowModelSaveFailure()
        {
            ChatModelSaveStatusText.Text = "Không thể lưu model đã chọn.";
            ChatModelSaveStatusText.Visibility = Visibility.Visible;
            _modelSaveFailureTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _modelSaveFailureTimer.Stop();
            _modelSaveFailureTimer.Tick -= OnModelSaveFailureTimerTick;
            _modelSaveFailureTimer.Tick += OnModelSaveFailureTimerTick;
            _modelSaveFailureTimer.Start();
        }

        private void OnModelSaveFailureTimerTick(object? sender, object e)
        {
            _modelSaveFailureTimer?.Stop();
            ChatModelSaveStatusText.Visibility = Visibility.Collapsed;
            ChatModelSaveStatusText.Text = string.Empty;
        }

        private int? GetCurrentModelContextLength()
        {
            if (_groupedCatalogs is null || string.IsNullOrWhiteSpace(_selectedModel)) return null;
            var state = _groupedCatalogs.State;
            if (string.IsNullOrWhiteSpace(state.SelectedBackend)) return null;

            if (state.Catalogs.TryGetValue(state.SelectedBackend, out var catalogState))
            {
                var match = catalogState.Catalog.FirstOrDefault(m => string.Equals(m.Id, _selectedModel, StringComparison.OrdinalIgnoreCase));
                if (match?.ContextLength is int len && len > 0)
                {
                    return len;
                }
            }
            return null;
        }

        public void UpdateContextUsage()
        {
            string currentModel = _selectedModel;
            int? catalogCtx = GetCurrentModelContextLength();
            var (maxTokens, displayLimit) = ModelContextSpecs.GetContextSpec(currentModel, catalogCtx);

            int totalChars = 0;
            int totalImages = 0;

            foreach (var msg in Messages)
            {
                if (!string.IsNullOrEmpty(msg.Content))
                {
                    totalChars += msg.Content.Length;
                }
                if (msg.Attachments != null)
                {
                    totalImages += msg.Attachments.Count;
                }
            }

            foreach (var att in PendingAttachments)
            {
                totalImages++;
            }

            int estimatedTokens = ModelContextSpecs.EstimateTokens(totalChars, totalImages);

            if (maxTokens > 0)
            {
                double percentage = Math.Min(100.0, (estimatedTokens * 100.0) / maxTokens);

                ContextProgressRing.Value = percentage;
                ContextProgressBar.Value = percentage;
                ContextProgressBar.Visibility = Visibility.Visible;

                var brush = percentage > 90
                    ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuDangerBrush"]
                    : percentage > 70
                        ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuWarningBrush"]
                        : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuAccentBrush"];

                ContextProgressRing.Foreground = brush;
                ContextProgressBar.Foreground = brush;

                ContextUsedLabel.Text = $"{ModelContextSpecs.FormatTokens(estimatedTokens)} / {displayLimit}";
                ContextPercentLabel.Text = $"{percentage:0.#}%";
                ContextPercentLabel.Foreground = percentage > 70 ? brush : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"];
            }
            else
            {
                ContextProgressRing.Value = 0;
                ContextProgressBar.Visibility = Visibility.Collapsed;
                ContextUsedLabel.Text = $"{ModelContextSpecs.FormatTokens(estimatedTokens)} tokens";
                ContextPercentLabel.Text = "Đã dùng";
                ContextPercentLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"];
            }
        }

        private void OnChatInputTyping(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(ChatInputTextBox.Text))
            {
                _typingDebounce?.Stop();
                if (_typingPushed)
                {
                    _typingPushed = false;
                    _ = _apiService?.PushVtsActivityAsync(false);
                }
                return;
            }
            _typingDebounce?.Stop();
            _typingDebounce?.Start();
        }

        private void OnAgentControllerChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateAgentInlineState();
                return;
            }

            DispatcherQueue.TryEnqueue(UpdateAgentInlineState);
        }

        private void OnAgentActivityReceived(object? sender, AgentActivityModel activity)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                ApplyAgentActivity(activity);
                return;
            }

            DispatcherQueue.TryEnqueue(() => ApplyAgentActivity(activity));
        }

        private void ApplyAgentActivity(AgentActivityModel activity)
        {
            if (_agentController?.IsBusy != true
                || string.IsNullOrWhiteSpace(activity.ActivityId))
            {
                return;
            }

            if (activity.Kind == RuAgent.Protocol.Harness.AgentActivityKind.Thinking && activity.State == RuAgent.Protocol.Harness.AgentActivityState.Completed)
            {
                if (_agentActivityById.Remove(activity.ActivityId, out var thinkingItem))
                {
                    AgentActivities.Remove(thinkingItem);
                }

                if (AgentActivities.Count == 0 && AgentActivityFeed is not null)
                {
                    AgentActivityFeed.Visibility = Visibility.Collapsed;
                }
                return;
            }

            if (_agentActivityById.TryGetValue(activity.ActivityId, out var existing))
            {
                existing.UpdateFrom(activity);
            }
            else
            {
                var item = new AgentActivityItem(activity);
                _agentActivityById.Add(item.ActivityId, item);
                AgentActivities.Add(item);
            }

            if (AgentActivityFeed is not null)
            {
                AgentActivityFeed.Visibility = AgentActivities.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            ScrollChatToBottom();
        }

        private void RemoveThinkingActivities()
        {
            if (AgentActivities.Count == 0)
            {
                return;
            }

            var thinkingItems = AgentActivities.Where(a => a.Kind == RuAgent.Protocol.Harness.AgentActivityKind.Thinking).ToList();
            if (thinkingItems.Count > 0)
            {
                foreach (var item in thinkingItems)
                {
                    _agentActivityById.Remove(item.ActivityId);
                    AgentActivities.Remove(item);
                }

                if (AgentActivities.Count == 0 && AgentActivityFeed is not null)
                {
                    AgentActivityFeed.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void OnAgentSummaryReceived(object? sender, AgentRunSummaryModel summary)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                ApplyAgentRunSummary(summary);
                return;
            }

            DispatcherQueue.TryEnqueue(() => ApplyAgentRunSummary(summary));
        }

        private void ApplyAgentRunSummary(AgentRunSummaryModel summary)
        {
            if (_agentController?.IsBusy != true)
            {
                return;
            }

            var item = new AgentRunSummaryItem(summary);
            if (!item.IsFailure)
            {
                // Success and cancelled runs keep the transcript clean; the
                // assistant reply itself is the outcome the user reads.
                return;
            }

            _agentRunSummary = item;
            AgentRunSummaryText.Text = _agentRunSummary.DisplayText;
            AgentRunSummaryMetadata.Text = _agentRunSummary.MetadataText;
            AgentRunSummaryGlyph.Glyph = _agentRunSummary.StatusGlyph;
            AgentRunSummaryGlyph.Foreground = _agentRunSummary.IsVerifiedSuccess
                ? (Brush)Application.Current.Resources["RuSuccessBrush"]
                : _agentRunSummary.IsFailure
                    ? (Brush)Application.Current.Resources["AppDangerBrush"]
                    : (Brush)Application.Current.Resources["RuMutedBrush"];
            AgentRunSummaryPanel.Visibility = Visibility.Visible;
            ScrollChatToBottom();
        }

        private void OnAgentAssistantDeltaReceived(object? sender, AgentAssistantDeltaModel delta)
        {
            // Voice delivery must not be gated by render coalescing
            if (_activeAgentTurnSource == AgentTurnSource.Voice
                && _voicePlayback?.AutoReadReplies == true)
            {
                // Deliver voice promptly on any thread that received the delta, but ensure the run is still current
                var runForVoice = _agentController?.CurrentRun;
                if (runForVoice != null && _activeAgentAssistantMessage != null && string.Equals(_activeAgentRunId ?? runForVoice.RunId, runForVoice.RunId, StringComparison.Ordinal))
                {
                    _voicePlayback.AppendReplyDelta(delta.Text);
                }
            }

            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnAgentAssistantDeltaReceived(sender, delta));
                return;
            }

            AgentRunModel? run = _agentController?.CurrentRun;
            if (run is null || _agentController?.IsBusy != true)
            {
                return;
            }

            if (_activeAgentAssistantMessage is null)
            {
                return;
            }

            _activeAgentRunId ??= run.RunId;
            if (!string.Equals(_activeAgentRunId, run.RunId, StringComparison.Ordinal))
            {
                return;
            }

            string key = $"{run.RunId}:{delta.ModelStepId}:{delta.TransientSequence}";
            if (!_agentDeltaKeys.Add(key))
            {
                return;
            }

            // Use accumulator as single authority for dedup + text; also keep legacy set for now
            if (!_replyAccumulator.TryAppend(run.RunId, delta.ModelStepId, delta.TransientSequence, delta.Text))
            {
                // If no active accumulator (e.g., after Invalidate), begin with real runId and retry once
                if (_activeAgentRunId != null && run.RunId == _activeAgentRunId)
                {
                    _replyAccumulator.Begin(_activeSessionId, run.RunId);
                    if (!_replyAccumulator.TryAppend(run.RunId, delta.ModelStepId, delta.TransientSequence, delta.Text))
                    {
                        return;
                    }
                }
                else
                {
                    return;
                }
            }

            _activeAgentReplyReceived = true;
            RemoveThinkingActivities();
            _replyRenderScheduler.Schedule();
        }

        private void ApplyReplySnapshot(AgentReplySnapshot snapshot)
        {
            if (_activeAgentAssistantMessage == null
                || !string.Equals(snapshot.SessionId, _activeSessionId, StringComparison.Ordinal)
                || !string.Equals(snapshot.RunId, _activeAgentRunId, StringComparison.Ordinal))
            {
                return;
            }

            if (!Messages.Contains(_activeAgentAssistantMessage))
            {
                Messages.Add(_activeAgentAssistantMessage);
            }

            RemoveThinkingActivities();
            _activeAgentAssistantMessage.Content = snapshot.Text;
            _activeAgentAssistantMessage.IsStreaming = true;
            ScrollChatToBottom();
        }

        private void ClearAgentActivities()
        {
            _breathingStoryboards.Clear();
            _agentActivityById.Clear();
            AgentActivities.Clear();
            if (AgentActivityFeed is not null)
            {
                AgentActivityFeed.Visibility = Visibility.Collapsed;
            }
        }

        // ── Activity text breathing animation ──────────────────────────────
        private readonly Dictionary<TextBlock, Storyboard> _breathingStoryboards = new();

        private void OnActivityItemLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid grid || grid.DataContext is not AgentActivityItem item)
                return;

            TextBlock? displayText = null;
            foreach (var child in grid.Children)
            {
                if (child is TextBlock tb && Grid.GetColumn(tb) == 0)
                {
                    displayText = tb;
                    break;
                }
            }
            if (displayText == null) return;

            ApplyActivityTextStyle(displayText, item);

            PropertyChangedEventHandler handler = (s, args) =>
            {
                if (args.PropertyName == nameof(AgentActivityItem.State) && s is AgentActivityItem ai)
                {
                    DispatcherQueue?.TryEnqueue(() => ApplyActivityTextStyle(displayText, ai));
                }
            };
            item.PropertyChanged += handler;
            grid.Tag = handler;
        }

        private void OnActivityItemUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid grid) return;

            if (grid.Tag is PropertyChangedEventHandler handler && grid.DataContext is AgentActivityItem item)
            {
                item.PropertyChanged -= handler;
            }

            foreach (var child in grid.Children)
            {
                if (child is TextBlock tb && Grid.GetColumn(tb) == 0)
                {
                    StopTextBreathing(tb);
                    break;
                }
            }
            grid.Tag = null;
        }

        private void ApplyActivityTextStyle(TextBlock textBlock, AgentActivityItem item)
        {
            if (item.IsRunning)
            {
                StartTextBreathing(textBlock);
            }
            else
            {
                StopTextBreathing(textBlock);
                if (item.IsFailed)
                {
                    textBlock.Foreground = (SolidColorBrush)Application.Current.Resources["RuDangerBrush"];
                }
                else
                {
                    textBlock.Foreground = (SolidColorBrush)Application.Current.Resources["RuMutedBrush"];
                }
            }
        }

        private void StartTextBreathing(TextBlock textBlock)
        {
            if (_breathingStoryboards.ContainsKey(textBlock)) return;

            var mutedColor = ((SolidColorBrush)Application.Current.Resources["RuMutedBrush"]).Color;
            var inkColor = ((SolidColorBrush)Application.Current.Resources["RuInkBrush"]).Color;

            var brush = new SolidColorBrush(mutedColor);
            textBlock.Foreground = brush;

            var colorAnim = new ColorAnimation
            {
                From = mutedColor,
                To = inkColor,
                Duration = new Duration(TimeSpan.FromMilliseconds(1400)),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(colorAnim, brush);
            Storyboard.SetTargetProperty(colorAnim, "Color");

            var sb = new Storyboard();
            sb.Children.Add(colorAnim);
            _breathingStoryboards[textBlock] = sb;
            sb.Begin();
        }

        private void StopTextBreathing(TextBlock textBlock)
        {
            if (_breathingStoryboards.Remove(textBlock, out var sb))
            {
                sb.Stop();
            }
        }

        private void ClearAgentRunSummary()
        {
            _agentRunSummary = null;
            if (AgentRunSummaryPanel is not null)
            {
                AgentRunSummaryPanel.Visibility = Visibility.Collapsed;
                AgentRunSummaryText.Text = string.Empty;
                AgentRunSummaryMetadata.Text = string.Empty;
            }
        }

        private void ShowAgentInlineStatus(string status)
        {
            _agentActivityVisible = false;
            AgentStatusText.Text = status;
        }

        private void UpdateAgentComposerVisuals()
        {
            if (AgentModeLabel is null)
            {
                return;
            }

            AgentModeLabel.Text = "Agent";
            AgentModeIcon.Glyph = "\uEC25";
            AgentModeButton.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(
                AgentModeButton,
                "Coding Agent");
            MicButton.IsEnabled = true;
            AutomationProperties.SetName(
                MicButton,
                "Bắt đầu trò chuyện bằng giọng nói");
        }

        private bool _isSendButtonInStopMode;

        private void UpdateSendButtonMode(bool isAgentRunning)
        {
            if (SendButton is null) return;

            _isSendButtonInStopMode = isAgentRunning;

            if (isAgentRunning)
            {
                if (SendButtonIcon is not null)
                {
                    SendButtonIcon.Glyph = "\uE71A";
                    SendButtonIcon.Margin = new Thickness(0);
                    if (Application.Current.Resources.TryGetValue("RuDangerBrush", out var dangerBrush) && dangerBrush is Brush danger)
                    {
                        SendButtonIcon.Foreground = danger;
                    }
                }
                AutomationProperties.SetName(SendButton, "Dừng Coding Agent");
            }
            else
            {
                if (SendButtonIcon is not null)
                {
                    SendButtonIcon.Glyph = "\uE725";
                    SendButtonIcon.Margin = new Thickness(-1, 1, 0, 0);
                    if (Application.Current.Resources.TryGetValue("RuInkBrush", out var inkBrush) && inkBrush is Brush ink)
                    {
                        SendButtonIcon.Foreground = ink;
                    }
                }
                AutomationProperties.SetName(SendButton, "Gửi tin nhắn");
            }
        }

        private void UpdateAgentInlineState()
        {
            if (_agentController is null)
            {
                AgentInlinePanel.Visibility = _agentActivityVisible ? Visibility.Visible : Visibility.Collapsed;
                if (!_agentActivityVisible)
                {
                    AgentStatusText.Text = string.Empty;
                }
                AgentStopButton.Visibility = Visibility.Collapsed;
                AgentContinueButton.Visibility = Visibility.Collapsed;
                AgentApprovalPanel.Visibility = Visibility.Collapsed;
                UpdateSendButtonMode(isAgentRunning: false);
                return;
            }

            if (_agentController.CurrentRun is { } currentRun
                && !string.Equals(currentRun.SessionId, _activeSessionId, StringComparison.Ordinal))
            {
                AgentInlinePanel.Visibility = _agentActivityVisible ? Visibility.Visible : Visibility.Collapsed;
                if (!_agentActivityVisible)
                {
                    AgentStatusText.Text = string.Empty;
                }
                AgentStopButton.Visibility = Visibility.Collapsed;
                AgentContinueButton.Visibility = Visibility.Collapsed;
                AgentApprovalPanel.Visibility = Visibility.Collapsed;
                UpdateSendButtonMode(isAgentRunning: false);
                return;
            }

            if (_agentController.IsBusy != true
                && !_agentController.CanCancel
                && !_agentController.HasPendingApproval
                && !_agentController.NeedsWorkspaceTrust
                && !_agentController.CanContinue)
            {
                _agentActivityVisible = false;
                AgentInlinePanel.Visibility = Visibility.Collapsed;
                AgentStatusText.Text = string.Empty;
                AgentStopButton.Visibility = Visibility.Collapsed;
                AgentContinueButton.Visibility = Visibility.Collapsed;
                AgentApprovalPanel.Visibility = Visibility.Collapsed;
                UpdateSendButtonMode(isAgentRunning: false);
                return;
            }

            bool isRunning = _agentController.CanCancel || IsActiveAgentRunPending();
            UpdateSendButtonMode(isAgentRunning: isRunning);

            string safeStatus = SafeAgentStatus(_agentController.StatusText);
            AgentStatusText.Text = safeStatus;
            AgentStopButton.Visibility = Visibility.Collapsed;
            AgentContinueButton.Visibility = _agentController.CanContinue
                ? Visibility.Visible
                : Visibility.Collapsed;

            // Workspace trust is auto-granted: a project-root chat starts silently.
            if (_agentController.NeedsWorkspaceTrust)
            {
                _ = _agentController.TrustAsync(approve: true);
                AgentApprovalPanel.Visibility = Visibility.Collapsed;
                return;
            }

            bool approvalPending = _agentController.HasPendingApproval;
            AgentApprovalPanel.Visibility = approvalPending ? Visibility.Visible : Visibility.Collapsed;
            if (approvalPending)
            {
                AgentApprovalSummaryText.Text = RenderApprovalSummary(
                    _agentController.PendingApprovalSummary);
            }

            bool hasInlineAction = approvalPending || _agentController.CanContinue;
            AgentInlinePanel.Visibility = hasInlineAction ? Visibility.Visible : Visibility.Collapsed;

            bool actionsEnabled = !_agentActionPending;
            AgentApproveButton.IsEnabled = actionsEnabled;
            AgentDenyButton.IsEnabled = actionsEnabled;
            AgentContinueButton.IsEnabled = actionsEnabled;
            AgentStopButton.IsEnabled = actionsEnabled;
        }

        /// <summary>
        /// Server-selected pending approval reason drives the bounded copy.
        /// The Host is the only authority, so we never echo the raw argv, script,
        /// working directory, or secrets. Reason codes come from the broker
        /// (external_directory_required, terminal_delete_requires_approval,
        /// terminal_unknown_executable); unknown values stay generic.
        /// </summary>
        private static string RenderApprovalSummary(string? summary)
        {
            string trimmed = BoundedDisplay(summary ?? string.Empty, 64);
            if (string.Equals(trimmed, "external_directory_required", StringComparison.Ordinal))
            {
                return "Ru cần dùng thư mục ngoài project trong đoạn chat này, cho đến khi ứng dụng đóng.";
            }
            if (string.Equals(trimmed, "terminal_delete_requires_approval", StringComparison.Ordinal))
            {
                return "Lệnh terminal này sẽ xóa file.";
            }
            if (string.Equals(trimmed, "terminal_unknown_executable", StringComparison.Ordinal))
            {
                return "Ru chưa nhận diện được executable của lệnh terminal này.";
            }
            if (string.IsNullOrEmpty(trimmed))
            {
                return "Agent đang yêu cầu một hành động cần phê duyệt.";
            }
            return trimmed;
        }

        private static string SafeAgentStatus(string? status)
        {
            if (string.IsNullOrWhiteSpace(status)
                || string.Equals(status.Trim(), "Phiên chạy đã kết thúc.", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            string trimmed = status.Trim();
            if (trimmed.StartsWith("Lỗi", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                return "Coding Agent gặp lỗi. Mở Công cụ → Nhật ký hoạt động để xem trạng thái.";
            }

            return BoundedDisplay(trimmed, 256);
        }

        private static string BoundedDisplay(string value, int maxLength)
        {
            string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
        }

        private bool IsActiveAgentRunPending()
        {
            if (_agentController is null)
            {
                return false;
            }

            AgentRunModel? run = _agentController.CurrentRun;
            return run is not null
                && string.Equals(run.SessionId, _activeSessionId, StringComparison.Ordinal)
                && (_agentController.CanCancel
                    || _agentController.HasPendingApproval
                    || _agentController.NeedsWorkspaceTrust);
        }

        private void ResetAgentState()
        {
            ClearAgentActivities();
            ClearAgentRunSummary();

            var monitor = _agentMonitorCts;
            _agentMonitorCts = null;
            monitor?.Cancel();
            monitor?.Dispose();
            _agentActivityVisible = false;
            _agentActionPending = false;
            AbandonActiveAgentReply(notifyVoice: true);

            UpdateAgentComposerVisuals();
            AgentInlinePanel.Visibility = Visibility.Collapsed;
            AgentApprovalPanel.Visibility = Visibility.Collapsed;
            AgentStopButton.Visibility = Visibility.Collapsed;
            AgentContinueButton.Visibility = Visibility.Collapsed;
            AgentStatusText.Text = string.Empty;
            UpdateSendButtonMode(isAgentRunning: false);
        }


        private static string StripDisplayMarkers(string content)
        {
            return Regex.Replace(content, VtsMarkerPattern, "").Trim();
        }

        private async void OnSuggestionClick(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
            {
                return;
            }

            var promptText = button.Tag as string;
            if (string.IsNullOrWhiteSpace(promptText))
            {
                return;
            }

            ChatInputTextBox.Text = promptText;
            await SendCurrentMessageAsync();
        }

        private async void OnSendClick(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_isSendButtonInStopMode || IsActiveAgentRunPending() || _agentController?.CanCancel == true)
                {
                    await StopActiveAgentAsync();
                    return;
                }

                await SendCurrentMessageAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnSendClick: {ex.Message}");
            }
        }

        private async void OnChatInputPreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            try
            {
                if (e.Key == Windows.System.VirtualKey.V)
                {
                    var ctrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                    if (ctrlDown)
                    {
                        var package = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                        if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap)
                            || package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                        {
                            e.Handled = true;
                            await TryPasteImageFromClipboardAsync();
                            return;
                        }
                    }
                }

                if (e.Key == Windows.System.VirtualKey.Enter)
                {
                    var shiftDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                    if (!shiftDown)
                    {
                        e.Handled = true;
                        if (_isSendButtonInStopMode || IsActiveAgentRunPending())
                        {
                            return;
                        }
                        await SendCurrentMessageAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OnChatInputPreviewKeyDown: {ex.Message}");
            }
        }

        private async void OnAttachImageClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeFilter.Add("*");

                if (MainWindow.Instance != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                }

                var files = await picker.PickMultipleFilesAsync();
                if (files != null && files.Count > 0)
                {
                    foreach (var file in files)
                    {
                        await AddFileAttachmentAsync(file);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatView] Error picking files: {ex.Message}");
            }
        }

        private void OnRemoveAttachmentClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var item = PendingAttachments.FirstOrDefault(a => a.Id == id);
                if (item != null)
                {
                    PendingAttachments.Remove(item);
                }
                if (PendingAttachments.Count == 0)
                {
                    AttachmentPreviewContainer.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void OnInputDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Thả để đính kèm tệp tin / hình ảnh";
                e.DragUIOverride.IsContentVisible = true;
            }
        }

        private async void OnInputDrop(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var items = await e.DataView.GetStorageItemsAsync();
                foreach (var storageItem in items)
                {
                    if (storageItem is Windows.Storage.StorageFile file)
                    {
                        await AddFileAttachmentAsync(file);
                    }
                }
            }
        }

        private async Task TryPasteImageFromClipboardAsync()
        {
            try
            {
                var package = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
                {
                    if (PendingAttachments.Count(static a => a.IsImage) >= 4)
                    {
                        ShowAgentInlineStatus("Tối đa 4 ảnh cho mỗi lượt gửi; hãy xoá bớt ảnh trước khi thêm.");
                        return;
                    }

                    var streamRef = await package.GetBitmapAsync();
                    using var sourceStream = await streamRef.OpenReadAsync();
                    if (sourceStream.Size > (ulong)MaxImageAttachmentBytes)
                    {
                        ShowAgentInlineStatus("Ảnh clipboard vượt quá kích thước 10MB cho phép.");
                        return;
                    }

                    var (bytes, mimeType, width, height) = await ImageAttachmentResizer.ResizeIfNeededAsync(sourceStream, "image/png");
                    string base64 = Convert.ToBase64String(bytes);

                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    using (var previewMs = new InMemoryRandomAccessStream())
                    {
                        using (var writer = new DataWriter(previewMs.GetOutputStreamAt(0)))
                        {
                            writer.WriteBytes(bytes);
                            await writer.StoreAsync();
                        }
                        previewMs.Seek(0);
                        await bitmap.SetSourceAsync(previewMs);
                    }

                    var item = new ImageAttachmentItem
                    {
                        FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                        ByteSize = bytes.Length,
                        Base64Data = base64,
                        MimeType = mimeType,
                        IsImage = true,
                        PreviewImage = bitmap,
                        ThumbnailImage = bitmap
                    };

                    PendingAttachments.Add(item);
                    AttachmentPreviewContainer.Visibility = Visibility.Visible;
                }
                else if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                {
                    var items = await package.GetStorageItemsAsync();
                    foreach (var storageItem in items)
                    {
                        if (storageItem is Windows.Storage.StorageFile file)
                        {
                            await AddFileAttachmentAsync(file);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatView] Clipboard image paste error: {ex.Message}");
            }
        }

        private async Task AddFileAttachmentAsync(Windows.Storage.StorageFile file)
        {
            try
            {
                var props = await file.GetBasicPropertiesAsync();
                bool isImage = IsImageFile(file.Name);
                if (!isImage && !IsTextOrCodeFile(file.Name))
                {
                    ShowAgentInlineStatus($"Tệp '{file.Name}' chưa được hỗ trợ. Chỉ nhận ảnh và tệp văn bản/mã nguồn.");
                    return;
                }

                if (isImage)
                {
                    if (PendingAttachments.Count(static a => a.IsImage) >= 4)
                    {
                        ShowAgentInlineStatus("Tối đa 4 ảnh cho mỗi lượt gửi; hãy xoá bớt ảnh trước khi thêm.");
                        return;
                    }

                    if (props.Size > MaxImageAttachmentBytes)
                    {
                        ShowAgentInlineStatus($"Ảnh '{file.Name}' vượt quá kích thước 10MB cho phép.");
                        return;
                    }

                    string inputMime = Services.ChatAttachmentPolicy.MimeForExtension(file.Name);

                    using var sourceStream = await file.OpenReadAsync();
                    if (sourceStream.Size > (ulong)MaxImageAttachmentBytes)
                    {
                        ShowAgentInlineStatus($"Ảnh '{file.Name}' vượt quá kích thước 10MB cho phép.");
                        return;
                    }

                    var (bytes, mimeType, width, height) = await ImageAttachmentResizer.ResizeIfNeededAsync(sourceStream, inputMime);
                    string base64 = Convert.ToBase64String(bytes);

                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    using (var previewMs = new InMemoryRandomAccessStream())
                    {
                        using (var writer = new DataWriter(previewMs.GetOutputStreamAt(0)))
                        {
                            writer.WriteBytes(bytes);
                            await writer.StoreAsync();
                        }
                        previewMs.Seek(0);
                        await bitmap.SetSourceAsync(previewMs);
                    }

                    var item = new ImageAttachmentItem
                    {
                        FileName = file.Name,
                        FilePath = file.Path,
                        ByteSize = bytes.Length,
                        Base64Data = base64,
                        MimeType = mimeType,
                        IsImage = true,
                        PreviewImage = bitmap,
                        ThumbnailImage = bitmap
                    };

                    PendingAttachments.Add(item);
                }
                else
                {
                    if (props.Size > MaxTextAttachmentBytes)
                    {
                        ShowAgentInlineStatus($"Tệp '{file.Name}' vượt quá kích thước 5MB cho phép.");
                        return;
                    }

                    string? textContent = null;
                    if (IsTextOrCodeFile(file.Name))
                    {
                        try
                        {
                            using var readStream = (await file.OpenReadAsync()).AsStreamForRead();
                            var bytes = await ReadBoundedBytesAsync(readStream, MaxTextAttachmentBytes);
                            if (bytes is null)
                            {
                                ShowAgentInlineStatus($"Tệp '{file.Name}' vượt quá kích thước 5MB cho phép.");
                                return;
                            }

                            using var textStream = new System.IO.MemoryStream(bytes, writable: false);
                            using var reader = new System.IO.StreamReader(textStream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                            textContent = await reader.ReadToEndAsync();
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ChatView] Could not read file as UTF-8: {ex.Message}");
                        }
                    }

                    var item = new ImageAttachmentItem
                    {
                        FileName = file.Name,
                        FilePath = file.Path,
                        ByteSize = (long)props.Size,
                        TextContent = textContent,
                        IsImage = false,
                        MimeType = "application/octet-stream"
                    };

                    PendingAttachments.Add(item);
                }

                AttachmentPreviewContainer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatView] Error adding attachment: {ex.Message}");
            }
        }

        private static Task<byte[]?> ReadBoundedBytesAsync(System.IO.Stream stream, int maximumBytes) =>
            Services.ChatAttachmentPolicy.ReadBoundedBytesAsync(stream, maximumBytes);

        private static bool IsImageFile(string name) => Services.ChatAttachmentPolicy.IsImageFile(name);

        private static bool IsTextOrCodeFile(string name) => Services.ChatAttachmentPolicy.IsTextOrCodeFile(name);

        private async Task SendCurrentMessageAsync()
        {
            try
            {
                string text = ChatInputTextBox.Text.Trim();
                var attachments = PendingAttachments.ToList();

                if (string.IsNullOrEmpty(text) && attachments.Count == 0) return;

                int imageCount = attachments.Count(static attachment => attachment.IsImage);
                if (imageCount > 4)
                {
                    ShowAgentInlineStatus("Tối đa 4 ảnh cho mỗi lượt gửi; hãy xoá bớt ảnh trước khi gửi.");
                    return;
                }

                ChatInputTextBox.Text = string.Empty;
                PendingAttachments.Clear();
                AttachmentPreviewContainer.Visibility = Visibility.Collapsed;

                if (_typingPushed)
                {
                    _typingPushed = false;
                    _ = _apiService?.PushVtsActivityAsync(false);
                }

                var textAttachments = attachments.Where(a => !a.IsImage && !string.IsNullOrWhiteSpace(a.TextContent)).ToList();
                string effectivePrompt = text;
                if (textAttachments.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var doc in textAttachments)
                    {
                        string lang = doc.FileExtension.TrimStart('.');
                        sb.AppendLine($"[Đính kèm tệp: {doc.FileName} ({doc.FormattedSize})]");
                        sb.AppendLine($"```{lang}");
                        sb.AppendLine(doc.TextContent);
                        sb.AppendLine("```");
                        sb.AppendLine();
                    }
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.AppendLine(text);
                    }
                    effectivePrompt = sb.ToString().TrimEnd();
                }

                await SendAgentTurnAsync(effectivePrompt, AgentTurnSource.Typed, CancellationToken.None, attachments: attachments, displayUserText: text);
            }
            catch (Exception ex)
            {
                FailActiveAgentReply(ex);
                ShowAgentError(ex);
            }
        }

        private async Task SendAgentTurnAsync(
            string text,
            AgentTurnSource source,
            CancellationToken cancellationToken,
            bool allowSessionRecovery = true,
            List<ImageAttachmentItem>? attachments = null,
            string? displayUserText = null)
        {
            if (allowSessionRecovery)
            {
                _agentSessionRecovery.Reset();
            }

            if (_agentController is null)
            {
                throw new InvalidOperationException("Agent controller is unavailable.");
            }

            if (IsActiveAgentRunPending())
            {
                ShowAgentInlineStatus("Coding Agent đang có phiên chạy. Hãy hoàn tất phiên hiện tại trước.");
                if (source == AgentTurnSource.Voice)
                {
                    throw new InvalidOperationException("Coding Agent đang xử lý lượt trước.");
                }

                return;
            }

            if (source == AgentTurnSource.Typed && _voiceConversation?.IsRunning == true)
            {
                await StopVoiceConversationAsync();
            }

            await EnsureAgentSessionAsync(cancellationToken);

            string workspace = AgentWorkspaceSelection.Resolve(null, _defaultAgentWorkspace)
                ?? Environment.CurrentDirectory;

            _agentWorkspace = workspace;

            try
            {
                // The run starts with the broker's effective Standard profile.
                // Session permissions are still decided server-side per request,
                // so the preset value here is compatibility metadata only, not
                // authority.
                var imagePayloads = attachments?
                    .Where(a => a.IsImage && (!a.IsImage || !string.IsNullOrEmpty(a.Base64Data) || !string.IsNullOrEmpty(a.FilePath)))
                    .Select(a => !string.IsNullOrEmpty(a.Base64Data) ? $"data:{a.MimeType};base64,{a.Base64Data}" : a.FilePath!)
                    .ToList();

                var request = new CreateAgentRunRequest(
                    _activeSessionId,
                    workspace,
                    text,
                    "standard",
                    0,
                    Guid.NewGuid().ToString("N"),
                    imagePayloads);

                _activeWorkspaceId = null;

                ClearAgentActivities();
                ClearAgentRunSummary();
                BeginAgentReply(source, text, attachments, displayUserText);
                ScrollChatToBottom();
                ShowAgentInlineStatus("Đang chuẩn bị thao tác…");
                AgentStopButton.Visibility = Visibility.Collapsed;
                UpdateSendButtonMode(isAgentRunning: true);
                UpdateAgentInlineState();
                await _agentController.CreateAndRunAsync(request, cancellationToken);
                _activeWorkspaceId = _agentController.CurrentRun?.WorkspaceId;
            }
            catch (AgentApiException error) when (
                allowSessionRecovery
                && AgentSessionRecovery.IsRecoverable(error)
                && _agentSessionRecovery.TryBegin(_activeSessionId))
            {
                // Preserve the voice completion gate while replacing the stale
                // session and retrying this same logical turn once.
                AbandonActiveAgentReply(notifyVoice: false);
                await RecoverAgentSessionAsync(cancellationToken);
                await SendAgentTurnAsync(
                    text,
                    source,
                    cancellationToken,
                    allowSessionRecovery: false,
                    attachments: attachments,
                    displayUserText: displayUserText);
                return;
            }

            if (IsTerminalStatus(_agentController.CurrentRun?.Status))
            {
                ClearAgentActivities();
                await CompleteActiveAgentTurnAsync(cancellationToken);
            }
            else
            {
                StartAgentMonitor();
            }
            UpdateAgentInlineState();
        }

        private void BeginAgentReply(AgentTurnSource source, string text, List<ImageAttachmentItem>? attachments = null, string? displayUserText = null)
        {
            _activeAgentTurnSource = source;
            _activeAgentRunId = null;
            _activeAgentReplyReceived = false;
            _activeAgentReplyCompleted = false;
            _agentDeltaKeys.Clear();
            _replyRenderScheduler.Invalidate();

            Messages.Add(new ChatMessageItem
            {
                Role = "user",
                Content = !string.IsNullOrEmpty(displayUserText) ? displayUserText : text,
                SessionId = _activeSessionId,
                Attachments = attachments != null && attachments.Count > 0
                    ? new ObservableCollection<ImageAttachmentItem>(attachments)
                    : new ObservableCollection<ImageAttachmentItem>()
            });

            _activeAgentAssistantMessage = new ChatMessageItem
            {
                Role = "assistant",
                Content = string.Empty,
                IsStreaming = true,
                SessionId = _activeSessionId,
            };

            if (source == AgentTurnSource.Voice)
            {
                _harnessEvents?.Write("voice.agent.sent");
            }

            if (_voicePlayback?.AutoReadReplies == true)
            {
                _voicePlayback.BeginReplyStream();
            }
        }

        private async Task CompleteActiveAgentTurnAsync(CancellationToken cancellationToken)
        {
            if (_activeAgentReplyCompleted)
            {
                return;
            }

            _activeAgentReplyCompleted = true;
            // Flush any pending coalesced render before completing
            AgentReplySnapshot? pending = _replyRenderScheduler.Flush();
            if (pending != null && _activeAgentAssistantMessage != null)
            {
                if (!Messages.Contains(_activeAgentAssistantMessage))
                {
                    Messages.Add(_activeAgentAssistantMessage);
                }
                _activeAgentAssistantMessage.Content = pending.Text;
                _activeAgentAssistantMessage.IsStreaming = false;
                ScrollChatToBottom();
            }
            else if (_activeAgentAssistantMessage != null)
            {
                _activeAgentAssistantMessage.IsStreaming = false;
            }
            ChatMessageItem? placeholder = _activeAgentAssistantMessage;

            string? latestAssistant = await ReloadSessionHistoryAsync(cancellationToken);
            string fallbackText = latestAssistant
                ?? StripDisplayMarkers(placeholder?.Content ?? string.Empty);
            if (_activeAgentTurnSource == AgentTurnSource.Voice)
            {
                _harnessEvents?.Write("voice.reply.received");
                if (_voicePlayback?.AutoReadReplies == true)
                {
                    // A streamed reply already entered the voice pipeline through
                    // OnAgentAssistantDeltaReceived. CompleteReplyStreamAsync only
                    // uses fallback text when no typed delta arrived.
                    await _voicePlayback.CompleteReplyStreamAsync(
                        _activeAgentReplyReceived ? string.Empty : fallbackText);
                }

                _harnessEvents?.Write("voice.reply.completed");
                _voiceConversation?.NotifyReplyCompleted();
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _agentDeltaKeys.Clear();
            _activeAgentTurnSource = AgentTurnSource.Typed;
            _agentActivityVisible = false;
            UpdateAgentInlineState();
        }

        private void FailActiveAgentReply(Exception exception)
        {
            _replyRenderScheduler.Invalidate();
            if (_activeAgentAssistantMessage is not null)
            {
                if (!Messages.Contains(_activeAgentAssistantMessage))
                {
                    Messages.Add(_activeAgentAssistantMessage);
                }
                _activeAgentAssistantMessage.Content = string.IsNullOrWhiteSpace(
                    _activeAgentAssistantMessage.Content)
                    ? "Coding Agent không thể hoàn thành lượt này. Hãy thử lại sau."
                    : $"{_activeAgentAssistantMessage.Content}\n\nCoding Agent không thể hoàn thành lượt này.";
                _activeAgentAssistantMessage.IsStreaming = false;
                _activeAgentAssistantMessage.IsError = true;
            }

            if (_activeAgentTurnSource == AgentTurnSource.Voice)
            {
                _voicePlayback?.FailReplyStream();
                _voiceConversation?.NotifyReplyError(exception);
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _agentDeltaKeys.Clear();
            _activeAgentTurnSource = AgentTurnSource.Typed;
        }

        private void AbandonActiveAgentReply(bool notifyVoice)
        {
            _replyRenderScheduler.Invalidate();
            _voicePlayback?.StopVoice();
            if (notifyVoice && _activeAgentTurnSource == AgentTurnSource.Voice)
            {
                _voiceConversation?.NotifyReplyError(
                    new OperationCanceledException("Agent turn was replaced or cancelled."));
            }

            if (_activeAgentAssistantMessage is not null)
            {
                _activeAgentAssistantMessage.IsStreaming = false;
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _activeAgentReplyReceived = false;
            _activeAgentReplyCompleted = false;
            _agentDeltaKeys.Clear();
            _activeAgentTurnSource = AgentTurnSource.Typed;
        }

        private async Task RecoverAgentSessionAsync(CancellationToken cancellationToken)
        {
            if (_apiService is null)
            {
                throw new InvalidOperationException("Backend session is unavailable.");
            }

            SessionInfo? created = await _apiService.CreateSessionAsync();
            cancellationToken.ThrowIfCancellationRequested();
            string? sessionId = created?.Id?.Trim();
            if (!AgentSessionSelection.IsValid(sessionId))
            {
                throw new AgentApiException(
                    "session_create_failed",
                    "Không tạo được session mới cho Coding Agent.");
            }

            _activeSessionId = sessionId!;
            ClearAgentActivities();
            ClearAgentRunSummary();
            Messages.Clear();
            UpdateViewMode();
            SessionOpened?.Invoke(
                this,
                new SessionOpenedEventArgs(
                    sessionId!,
                    created?.Title ?? "Cuộc trò chuyện mới"));
        }

        private async Task EnsureAgentSessionAsync(CancellationToken cancellationToken)
        {
            if (AgentSessionSelection.IsValid(_activeSessionId))
            {
                return;
            }

            if (_apiService is null)
            {
                throw new InvalidOperationException("Backend session is unavailable.");
            }

            SessionInfo? created = await _apiService.CreateSessionAsync();
            cancellationToken.ThrowIfCancellationRequested();
            string? sessionId = created?.Id?.Trim();
            if (!AgentSessionSelection.IsValid(sessionId))
            {
                throw new AgentApiException(
                    "session_create_failed",
                    "Không tạo được session cho Coding Agent.");
            }

            string validSessionId = sessionId!;
            _activeSessionId = validSessionId;
            SessionOpened?.Invoke(
                this,
                new SessionOpenedEventArgs(
                    validSessionId,
                    created?.Title ?? "Cuộc trò chuyện mới"));
        }

        private bool IsProjectRootWorkspace(string? workspace)
        {
            if (string.IsNullOrWhiteSpace(workspace))
            {
                return true;
            }

            string normalizedWorkspace = workspace.Trim().TrimEnd('\\', '/');

            if (!string.IsNullOrWhiteSpace(_defaultAgentWorkspace))
            {
                string normDefault = _defaultAgentWorkspace.Trim().TrimEnd('\\', '/');
                if (string.Equals(normalizedWorkspace, normDefault, StringComparison.OrdinalIgnoreCase)
                    || normalizedWorkspace.StartsWith(normDefault + "\\", StringComparison.OrdinalIgnoreCase)
                    || normalizedWorkspace.StartsWith(normDefault + "/", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            string currentDir = Environment.CurrentDirectory.Trim().TrimEnd('\\', '/');
            if (string.Equals(normalizedWorkspace, currentDir, StringComparison.OrdinalIgnoreCase)
                || normalizedWorkspace.StartsWith(currentDir + "\\", StringComparison.OrdinalIgnoreCase)
                || normalizedWorkspace.StartsWith(currentDir + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private async void OnImageAttachmentTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ImageAttachmentItem item && item.PreviewImage is not null && XamlRoot is not null)
            {
                var image = new Image
                {
                    Source = item.PreviewImage,
                    Stretch = Stretch.Uniform,
                    MaxWidth = 800,
                    MaxHeight = 600,
                };
                var dialog = new ContentDialog
                {
                    Title = string.IsNullOrWhiteSpace(item.FileName) ? "Hình ảnh đính kèm" : item.FileName,
                    Content = new ScrollViewer
                    {
                        Content = image,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                    CloseButtonText = "Đóng",
                    XamlRoot = XamlRoot,
                };
                await dialog.ShowAsync();
            }
        }

        private static bool IsTerminalStatus(string? status) => status is
            "completed" or "failed" or "cancelled" or "interrupted";

        private void ShowAgentError(Exception exception)
        {
            ClearAgentActivities();
            ClearAgentRunSummary();
            string status = exception switch
            {
                AgentApiException api => api.ErrorCode switch
                {
                    "invalid_workspace" => "Thư mục làm việc không hợp lệ hoặc không tồn tại.",
                    "workspace_busy" => "Thư mục đang được một phiên Agent khác sử dụng.",
                    "preset_mismatch" or "preset_version_conflict" => "Preset quyền đã thay đổi. Hãy chọn lại preset rồi thử lại.",
                    "session_create_failed" => "Không tạo được phiên trò chuyện cho Coding Agent. Kiểm tra backend rồi thử lại.",
                    "session_not_found" or "invalid_session_id" => "Phiên trò chuyện không còn hợp lệ. Hãy gửi lại để Ru tạo phiên mới.",
                    "unauthorized" => "Phiên đăng nhập Agent không còn hợp lệ. Hãy khởi động lại backend.",
                    "agent_store_unavailable" or "harness_store_unavailable" => "Cơ sở dữ liệu Agent Harness tạm thời không khả dụng. Kiểm tra quyền ghi hoặc khởi động lại.",
                    _ => !string.IsNullOrWhiteSpace(api.Message) && !string.Equals(api.Message, api.ErrorCode, StringComparison.OrdinalIgnoreCase)
                        ? $"Coding Agent không thể bắt đầu ({api.ErrorCode}: {api.Message}). Mở Công cụ → Nhật ký hoạt động để kiểm tra."
                        : $"Coding Agent không thể bắt đầu ({api.ErrorCode}). Mở Công cụ → Nhật ký hoạt động để kiểm tra."
                },
                _ => $"Coding Agent không thể bắt đầu: {exception.Message}"
            };
            ShowAgentInlineStatus(status);
            AgentStopButton.Visibility = Visibility.Collapsed;
            AgentApprovalPanel.Visibility = Visibility.Collapsed;
            AgentContinueButton.Visibility = Visibility.Collapsed;
        }

        private void StartAgentMonitor()
        {
            var previous = _agentMonitorCts;
            _agentMonitorCts = new CancellationTokenSource();
            previous?.Cancel();
            previous?.Dispose();
            _ = MonitorAgentRunAsync(_agentMonitorCts.Token);
        }

        private async Task MonitorAgentRunAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested && _agentController is not null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                    if (_agentController.CurrentRun is null)
                    {
                        return;
                    }

                    if (IsTerminalStatus(_agentController.CurrentRun.Status))
                    {
                        ClearAgentActivities();
                        await CompleteActiveAgentTurnAsync(cancellationToken);
                        return;
                    }

                    await _agentController.RefreshAsync(cancellationToken);
                    UpdateAgentInlineState();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                ShowAgentError(exception);
            }
        }

        private async Task<string?> ReloadSessionHistoryAsync(CancellationToken cancellationToken)
        {
            if (_apiService is null)
            {
                return null;
            }

            var history = await _apiService.GetSessionMessagesAsync(_activeSessionId);
            if (history is null || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            string? latestAssistant = history
                .LastOrDefault(item => string.Equals(item.Role, "assistant", StringComparison.Ordinal))
                .Content;
            latestAssistant = StripDisplayMarkers(latestAssistant ?? string.Empty);

            void Apply()
            {
                ClearAgentActivities();
                Messages.Clear();
                foreach (var (role, content) in history)
                {
                    Messages.Add(new ChatMessageItem
                    {
                        Role = role,
                        Content = role == "assistant" ? StripDisplayMarkers(content) : content
                    });
                }

                if (history.Count > 0)
                {
                    ScrollChatToBottom();
                }

                UpdateViewMode();
            }

            if (DispatcherQueue.HasThreadAccess)
            {
                Apply();
            }
            else
            {
                var applied = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                if (!DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        Apply();
                        applied.TrySetResult(true);
                    }
                    catch (Exception error)
                    {
                        applied.TrySetException(error);
                    }
                }))
                {
                    return null;
                }

                await applied.Task.ConfigureAwait(false);
            }

            return latestAssistant;
        }

        private async Task SendVoiceMessageAsync(string text, CancellationToken cancellationToken)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                await SendVoiceMessageOnUiThreadAsync(text, cancellationToken);
                return;
            }

            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await SendVoiceMessageOnUiThreadAsync(text, cancellationToken);
                    completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }))
            {
                throw new InvalidOperationException("Không thể chuyển lượt nói sang UI thread.");
            }

            await completion.Task.ConfigureAwait(false);
        }

        private async Task SendVoiceMessageOnUiThreadAsync(string text, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SendAgentTurnAsync(text, AgentTurnSource.Voice, cancellationToken);
            }
            catch (Exception ex)
            {
                FailActiveAgentReply(ex);
                ShowAgentError(ex);
                throw;
            }
        }

        private void ApplyInputWidth()
        {
            if (RootGrid == null || RootGrid.ActualWidth <= 0) return;

            double horizontalInset = RootGrid.ActualWidth < 900
                ? 24
                : RootGrid.ActualWidth < 1280
                    ? 48
                    : 96;
            double availableWidth = Math.Max(160, RootGrid.ActualWidth - horizontalInset);
            double targetWidth = Math.Min(ReadingColumnMaxWidth, availableWidth);

            if (WelcomeHeroPanel != null)
            {
                WelcomeHeroPanel.Width = targetWidth;
            }

            if (InputContainer != null)
            {
                InputContainer.Width = targetWidth;
            }

            if (InputBorder != null)
            {
                InputBorder.Width = targetWidth;
            }

            if (ChatContentGrid != null)
            {
                ChatContentGrid.Width = targetWidth;
            }

            if (ChatListView != null)
            {
                ChatListView.Width = targetWidth;
            }
        }

        private void ScrollChatToBottom()
        {
            ChatScrollHost.UpdateLayout();
            if (ChatScrollHost.ScrollableHeight > 0)
            {
                ChatScrollHost.ChangeView(null, ChatScrollHost.ScrollableHeight, null, disableAnimation: true);
                return;
            }
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RetryScrollAfterLayout);
        }

        private void RetryScrollAfterLayout()
        {
            ChatScrollHost.UpdateLayout();
            if (ChatScrollHost.ScrollableHeight > 0)
            {
                ChatScrollHost.ChangeView(null, ChatScrollHost.ScrollableHeight, null, disableAnimation: true);
            }
        }


        private async Task StopActiveAgentAsync(bool force = false)
        {
            if (force)
            {
                // Session replacement must not be blocked by another UI action
                // already in flight; the old run still has to be closed first.
                await (_agentController?.CancelAsync() ?? Task.CompletedTask);
            }
            else
            {
                await RunAgentActionAsync(
                    () => _agentController?.CancelAsync() ?? Task.CompletedTask,
                    "Đang dừng Coding Agent…");
            }
            AbandonActiveAgentReply(notifyVoice: true);
            ClearAgentActivities();
            ClearAgentRunSummary();
            UpdateSendButtonMode(isAgentRunning: false);
        }

        private async void OnAgentStopClick(object sender, RoutedEventArgs e)
        {
            await StopActiveAgentAsync();
        }

        private async void OnAgentContinueClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.ContinueAsync() ?? Task.CompletedTask,
                "Đang tiếp tục Coding Agent…");
        }

        private async void OnAgentApproveClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.ApproveAsync() ?? Task.CompletedTask,
                "Đang gửi phê duyệt…");
        }

        private async void OnAgentDenyClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.DenyAsync() ?? Task.CompletedTask,
                "Đang từ chối phê duyệt…");
        }

        private async Task RunAgentActionAsync(Func<Task> action, string status)
        {
            if (_agentActionPending || _agentController is null)
            {
                return;
            }

            _agentActionPending = true;
            ShowAgentInlineStatus(status);
            UpdateAgentInlineState();
            try
            {
                await action();
                StartAgentMonitor();
            }
            catch (Exception exception)
            {
                ShowAgentError(exception);
            }
            finally
            {
                _agentActionPending = false;
                UpdateAgentInlineState();
            }
        }

        public async Task OpenSessionAsync(string sessionId, string title)
        {
            try
            {
                if (_apiService == null) return;
                await ReplaceVoiceSessionAsync();
                // A workspace is single-owner while a run is non-terminal. Wait for
                // the cancellation commit before switching sessions; fire-and-forget
                // cancellation races the next create request and surfaces as the
                // misleading "workspace busy" error.
                await StopActiveAgentAsync(force: true);
                _activeSessionId = sessionId;
                ResetAgentState();
                SessionOpened?.Invoke(this, new SessionOpenedEventArgs(sessionId, title));
                Messages.Clear();
                var history = await _apiService.GetSessionMessagesAsync(_activeSessionId);
                if (history != null)
                {
                    foreach (var (role, content) in history)
                    {
                        Messages.Add(new ChatMessageItem
                        {
                            Role = role,
                            Content = role == "assistant" ? StripDisplayMarkers(content) : content
                        });
                    }
                    
                    if (history.Count > 0)
                    {
                        ScrollChatToBottom();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OpenSessionAsync: {ex.Message}");
                Messages.Add(new ChatMessageItem
                {
                    Role = "assistant",
                    Content = "**LỖI MỞ SESSION:** Không thể tải cuộc trò chuyện. Hãy kiểm tra backend rồi thử lại.",
                    IsError = true
                });
            }
        }

        private async void OnMicClick(object sender, RoutedEventArgs e)
        {
            if (_voiceConversation == null)
            {
                return;
            }

            try
            {
                if (_voiceConversation.IsRunning)
                {
                    await StopVoiceConversationAsync();
                    return;
                }

                _voicePlayback?.StopVoice();
                await _voiceConversation.StartAsync();
                SetVoiceButtonState(true);
            }
            catch (Exception ex)
            {
                SetVoiceButtonState(false);
                ChatInputTextBox.PlaceholderText = "Không truy cập được micro. Kiểm tra quyền mic.";
                System.Diagnostics.Debug.WriteLine($"Error starting voice conversation: {ex.Message}");
            }
        }

        private async Task StopVoiceConversationAsync()
        {
            if (_voiceConversation == null || !_voiceConversation.IsRunning)
            {
                _voicePlayback?.StopVoice();
                SetVoiceButtonState(false);
                return;
            }

            await _voiceConversation.StopAsync();
            AbandonActiveAgentReply(notifyVoice: false);
            _harnessEvents?.Write(
                string.Equals(_harnessScenario, "cancel", StringComparison.OrdinalIgnoreCase)
                    ? "voice.conversation.cancelled"
                    : "voice.conversation.stopped");
            _voicePlayback?.StopVoice();
            SetVoiceButtonState(false);
        }

        private async Task ReplaceVoiceSessionAsync()
        {
            if (_voiceConversation != null)
            {
                await _voiceConversation.ReplaceSessionAsync();
            }

            AbandonActiveAgentReply(notifyVoice: false);
            SetVoiceButtonState(false);
        }

        private async Task<string?> TranscribeVoiceAsync(byte[] wavBytes, CancellationToken cancellationToken)
        {
            if (_apiService == null)
            {
                throw new InvalidOperationException("Voice API chưa sẵn sàng.");
            }

            string? text = await _apiService.TranscribeAudioAsync(wavBytes, cancellationToken);
            if (text == null)
            {
                throw new InvalidOperationException("Dịch vụ nhận dạng giọng nói không khả dụng.");
            }

            _harnessEvents?.Write("voice.transcribe.completed");
            return text;
        }

        private void OnVoiceConversationStatusChanged(object? sender, VoiceConversationStatus status)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (status.State == VoiceConversationState.Stopped)
                {
                    SetVoiceButtonState(false);
                    VoiceConversationStatusText.Text = "Đã dừng";
                    VoiceConversationStatusText.Visibility = Visibility.Visible;
                    return;
                }

                if (status.State == VoiceConversationState.Error)
                {
                    SetVoiceButtonState(false);
                    ChatInputTextBox.PlaceholderText = status.Message
                        ?? "Cuộc trò chuyện bằng giọng nói đã dừng.";
                    VoiceConversationStatusText.Text = status.Message ?? "Không truy cập được micro.";
                    VoiceConversationStatusText.Visibility = Visibility.Visible;
                    return;
                }

                SetVoiceButtonState(true);
                VoiceConversationStatusText.Text = status.State switch
                {
                    VoiceConversationState.Listening => "Đang nghe...",
                    VoiceConversationState.Transcribing => "Đang xử lý...",
                    VoiceConversationState.Sending or VoiceConversationState.WaitingForReply => "Đang chờ Ru trả lời...",
                    _ => status.Message ?? string.Empty,
                };
                VoiceConversationStatusText.Visibility = Visibility.Visible;
                if (status.State == VoiceConversationState.Listening)
                {
                    _harnessEvents?.Write("voice.capture.started");
                }
                else if (status.State == VoiceConversationState.Transcribing)
                {
                    _harnessEvents?.Write("voice.capture.ended");
                }
                if (!string.IsNullOrWhiteSpace(status.Message))
                {
                    ChatInputTextBox.PlaceholderText = status.Message;
                }
            });
        }

        private void SetVoiceButtonState(bool active)
        {
            MicButton.IsEnabled = true;
            MicIcon.Glyph = active ? "\uE768" : "\uE720";
            MicIcon.FontSize = 14;
            MicIcon.Foreground = active
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : (Brush)Application.Current.Resources["RuMutedBrush"];
            MicButton.Background = active
                ? (SolidColorBrush)Application.Current.Resources["AppDangerBrush"]
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            AutomationProperties.SetName(
                MicButton,
                active ? "Dừng trò chuyện bằng giọng nói" : "Bắt đầu trò chuyện bằng giọng nói");
        }

        public void Dispose()
        {
            _agentMonitorCts?.Cancel();
            _agentMonitorCts?.Dispose();
            _agentMonitorCts = null;
            _modelSaveFailureTimer?.Stop();
            if (_providerCatalogController != null)
            {
                if (_ownsProviderCatalogController)
                {
                    _providerCatalogController.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                _providerCatalogController = null;
            }
            if (_groupedCatalogs != null)
            {
                _groupedCatalogs.StateChanged -= OnGroupedStateChanged;
                if (_ownsGroupedCatalogs)
                {
                    _groupedCatalogs.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                _groupedCatalogs = null;
            }
            if (_agentController is not null)
            {
                _agentController.PropertyChanged -= OnAgentControllerChanged;
                _agentController.ActivityReceived -= OnAgentActivityReceived;
                _agentController.SummaryReceived -= OnAgentSummaryReceived;
                _agentController.AssistantDeltaReceived -= OnAgentAssistantDeltaReceived;
            }
            ClearAgentActivities();
            ClearAgentRunSummary();
            _typingDebounce?.Stop();
            _typingDebounce = null;
            MeteorShowerField?.Dispose();
            _voicePlayback?.StopVoice();
            if (_voiceConversation != null)
            {
                _voiceConversation.StatusChanged -= OnVoiceConversationStatusChanged;
                _voiceConversation.Dispose();
                _voiceConversation = null;
            }
            _voiceCapture?.Dispose();
            _voiceCapture = null;
        }
    }
}
