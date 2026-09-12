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
using Windows.Foundation;
using RuAgent.Client;
using RuAgent.Harness.Core;
using RuAgent.Harness.Core.Prompting;
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
        private const double NarrowWidthThreshold = 900;
        private const double MediumWidthThreshold = 1280;
        private const double NarrowHorizontalInset = 24;
        private const double MediumHorizontalInset = 48;
        private const double WideHorizontalInset = 96;
        private const double MinAvailableInputWidth = 160;
        private const int MaxImageAttachmentBytes = Services.ChatAttachmentPolicy.MaxImageAttachmentBytes;
        private const int MaxTextAttachmentBytes = Services.ChatAttachmentPolicy.MaxTextAttachmentBytes;

        private ApiService? _apiService;
        private VoicePlaybackService? _voicePlayback;
        private VoiceTurnCaptureService? _voiceCapture;
        private VoiceConversationService? _voiceConversation;
        private HarnessEventSink? _harnessEvents;
        private string? _harnessScenario;
        private string _activeSessionId = "default";
        public string CurrentSessionId => _activeSessionId;
        private string _activeSessionTitle = "New Chat";
        public event EventHandler<SessionInfo>? SessionForked;
        private AgentRunController? _agentController;
        private CancellationTokenSource? _agentMonitorCts;
        private readonly AgentSessionRecovery _agentSessionRecovery = new();
        private bool _agentActivityVisible;
        private string _agentWorkspace = Environment.CurrentDirectory;
        private string? _defaultAgentWorkspace;
        private bool _agentActionPending;
        private bool _isSendingUserTurn;
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
        private AgentModelUsage? _activeAgentUsage;
        private readonly HashSet<string> _agentDeltaKeys = new(StringComparer.Ordinal);
        private readonly AgentReplyAccumulator _replyAccumulator = new();
        private readonly AgentReplyRenderScheduler _replyRenderScheduler;
        private readonly object _accumulatorLock = new();

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
                DispatcherTimer? contextHoverTimer = null;

                ContextMeterButton.PointerEntered += (s, e) =>
                {
                    ContextMeterButton.Background = canvas;
                    contextHoverTimer?.Stop();
                    contextHoverTimer = new DispatcherTimer
                    {
                        Interval = Services.UiTiming.ChatFlash
                    };
                    contextHoverTimer.Tick += (_, _) =>
                    {
                        contextHoverTimer?.Stop();
                        if (ContextWindowFlyout != null && !ContextWindowFlyout.IsOpen && ContextMeterButton != null)
                        {
                            try
                            {
                                ContextWindowFlyout.ShowAt(ContextMeterButton, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
                                {
                                    ShowMode = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.TransientWithDismissOnPointerMoveAway
                                });
                            }
                            catch
                            {
                                ContextWindowFlyout.ShowAt(ContextMeterButton);
                            }
                        }
                    };
                    contextHoverTimer.Start();
                };

                ContextMeterButton.PointerExited += (s, e) =>
                {
                    ContextMeterButton.Background = transparent;
                    contextHoverTimer?.Stop();
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
        private string _selectedBackend = string.Empty;
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
            _agentController?.ToolOutputReceived += OnAgentToolOutputReceived;
            _agentController?.UsageReceived += OnAgentUsageReceived;
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
            _typingDebounce = new DispatcherTimer { Interval = Services.UiTiming.TypingDebounce };
            _typingDebounce.Tick += (_, __) =>
            {
                _typingDebounce.Stop();
                if (!string.IsNullOrEmpty(ChatInputTextBox.Text) && !_typingPushed)
                {
                    _typingPushed = true;
                    PushSafelyAsync(_apiService?.PushVtsActivityAsync(true));
                }
            };

            Messages.CollectionChanged += (_, __) => UpdateContextUsage();
            PendingAttachments.CollectionChanged += (_, __) => UpdateContextUsage();

            _ = LoadChatModelsAsync();
        }

        public void ReinitializeBackend(
            ApiService apiService,
            AgentRunController? agentController,
            ProviderCatalogController? providerCatalogController,
            GroupedProviderCatalogController? groupedProviderCatalogs)
        {
            _apiService = apiService;
            if (_groupedCatalogs != null)
            {
                _groupedCatalogs.StateChanged -= OnGroupedStateChanged;
            }
            if (_agentController != null)
            {
                _agentController.PropertyChanged -= OnAgentControllerChanged;
                _agentController.ActivityReceived -= OnAgentActivityReceived;
                _agentController.SummaryReceived -= OnAgentSummaryReceived;
                _agentController.AssistantDeltaReceived -= OnAgentAssistantDeltaReceived;
                _agentController.ToolOutputReceived -= OnAgentToolOutputReceived;
                _agentController.UsageReceived -= OnAgentUsageReceived;
            }

            _agentController = agentController;
            _providerCatalogController = providerCatalogController ?? new ProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsProviderCatalogController = providerCatalogController == null;
            _groupedCatalogs = groupedProviderCatalogs ?? new GroupedProviderCatalogController(new ProviderCatalogService(apiService));
            _ownsGroupedCatalogs = groupedProviderCatalogs == null;
            _groupedCatalogs.StateChanged += OnGroupedStateChanged;

            if (_agentController != null)
            {
                _agentController.PropertyChanged += OnAgentControllerChanged;
                _agentController.ActivityReceived += OnAgentActivityReceived;
                _agentController.SummaryReceived += OnAgentSummaryReceived;
                _agentController.AssistantDeltaReceived += OnAgentAssistantDeltaReceived;
                _agentController.ToolOutputReceived += OnAgentToolOutputReceived;
                _agentController.UsageReceived += OnAgentUsageReceived;
            }

            UpdateAgentComposerVisuals();
            UpdateAgentInlineState();
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
                string currentModel = ProviderModelSelection.GetSelectedModel(settings, backend);

                _selectedModel = currentModel;
                _selectedBackend = backend;
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
            if (!string.IsNullOrWhiteSpace(state.SelectedBackend))
            {
                _selectedBackend = state.SelectedBackend;
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
                    _selectedBackend = backend;
                    // Reset effort so stale value from previous model/backend cannot
                    // stick when the new model has different levels. Fresh server
                    // effort arrives via grouped refresh below.
                    _selectedReasoningEffort = "default";
                    _groupedCatalogs?.SetSelection(backend, model);
                    ChatModelLabel.Text = ProviderModelPickerProjection.TriggerDisplay(backend, model);
                    UpdateContextUsage();
                    if (_providerCatalogController != null)
                    {
                        _providerCatalogController.SetBackend(backend);
                        _ = _providerCatalogController.RefreshAsync();
                    }
                    // Refresh grouped catalogs in background to sync SelectedEffort;
                    // picker itself uses local _selectedModel instantly (no restart).
                    _ = _groupedCatalogs?.RefreshAsync(force: true);
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
            SyncEffortFromCatalog();
            var options = GetCurrentReasoningOptions();

            if (!options.Any(o => string.Equals(o.Value, _selectedReasoningEffort, StringComparison.OrdinalIgnoreCase)))
            {
                _selectedReasoningEffort = "default";
            }

            ChatReasoningLabel.Text = _selectedReasoningEffort;
            AutomationProperties.SetName(ChatReasoningButton, $"Reasoning effort: {_selectedReasoningEffort}");
        }

        private IReadOnlyList<ProviderReasoningOption> GetCurrentReasoningOptions()
        {
            if (_groupedCatalogs is null)
            {
                return [new ProviderReasoningOption("default", "default")];
            }

            var groupedState = _groupedCatalogs.State;
            string backend = !string.IsNullOrWhiteSpace(groupedState.SelectedBackend)
                ? groupedState.SelectedBackend
                : _selectedBackend;
            if (string.IsNullOrWhiteSpace(backend))
            {
                return [new ProviderReasoningOption("default", "default")];
            }

            if (groupedState.Catalogs.TryGetValue(backend, out var catalogState))
            {
                string effectiveModel = !string.IsNullOrWhiteSpace(groupedState.SelectedModel)
                    ? groupedState.SelectedModel
                    : _selectedModel;
                return ProviderReasoningOptions.For(catalogState, effectiveModel);
            }

            return [new ProviderReasoningOption("default", "default")];
        }

        private void SyncEffortFromCatalog(bool force = false)
        {
            if (_groupedCatalogs is null) return;

            var groupedState = _groupedCatalogs.State;
            if (string.IsNullOrWhiteSpace(groupedState.SelectedBackend)) return;

            if (groupedState.Catalogs.TryGetValue(groupedState.SelectedBackend, out var catalogState))
            {
                if (!string.IsNullOrWhiteSpace(catalogState.SelectedEffort) &&
                    (force ||
                    string.Equals(_selectedReasoningEffort, "default", StringComparison.OrdinalIgnoreCase)))
                {
                    _selectedReasoningEffort = catalogState.SelectedEffort;
                }
            }
        }

        private void OnChatReasoningFlyoutOpened(object sender, object e)
        {
            ReasoningOptionsContainer.Children.Clear();
            SyncEffortFromCatalog();
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
                    try
                    {
                        ChatReasoningFlyout.Hide();
                        await ApplyReasoningEffortAsync(effort);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[ChatView] Reasoning effort selection failed: {ex.Message}");
                    }
                };

                ReasoningOptionsContainer.Children.Add(btn);
            }
        }

        private async Task ApplyReasoningEffortAsync(string effort)
        {
            string previousEffort = _selectedReasoningEffort;
            _selectedReasoningEffort = effort;
            ChatReasoningLabel.Text = effort;
            AutomationProperties.SetName(ChatReasoningButton, $"Reasoning effort: {effort}");

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
                AutomationProperties.SetName(ChatReasoningButton, $"Reasoning effort: {previousEffort} (Save failed)");
            }
        }

        private void ShowModelSaveFailure()
        {
            ChatModelSaveStatusText.Text = "Could not save selected model.";
            ChatModelSaveStatusText.Visibility = Visibility.Visible;
            _modelSaveFailureTimer ??= new DispatcherTimer { Interval = Services.UiTiming.ModelSaveFailure };
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
            string backend = !string.IsNullOrWhiteSpace(state.SelectedBackend)
                ? state.SelectedBackend
                : _selectedBackend;
            if (string.IsNullOrWhiteSpace(backend)) return null;

            if (state.Catalogs.TryGetValue(backend, out var catalogState))
            {
                string lookup = !string.IsNullOrWhiteSpace(state.SelectedModel)
                    ? state.SelectedModel
                    : _selectedModel;
                var match = catalogState.Catalog.FirstOrDefault(m => string.Equals(m.Id, lookup, StringComparison.OrdinalIgnoreCase));
                if (match?.ContextLength is int len && len > 0)
                {
                    return len;
                }
            }
            return null;
        }

        private bool _suppressContextUsageUpdate;

        private static int ResolveUsageTotal(AgentModelUsage usage)
        {
            if (usage.TotalTokens is { } total && total > 0) return total;
            int input = usage.InputTokens ?? 0;
            int output = usage.OutputTokens ?? 0;
            int cacheRead = usage.CacheReadInputTokens ?? 0;
            int cacheWrite = usage.CacheWriteInputTokens ?? 0;
            return input + output + cacheRead + cacheWrite;
        }

        public void UpdateContextUsage()
        {
            if (_suppressContextUsageUpdate) return;
            string currentModel = !string.IsNullOrWhiteSpace(_groupedCatalogs?.State.SelectedModel)
                ? _groupedCatalogs.State.SelectedModel
                : _selectedModel;
            int? catalogCtx = GetCurrentModelContextLength();
            var (maxTokens, displayLimit) = ModelContextSpecs.GetContextSpec(currentModel, catalogCtx);

            int totalChars = 0;
            int totalImages = 0;
            int reportedTokens = 0;

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
                if (msg.Usage is { } usage)
                {
                    reportedTokens = Math.Max(reportedTokens, ResolveUsageTotal(usage));
                }
            }

            if (_activeAgentUsage is { } activeUsage)
            {
                reportedTokens = Math.Max(reportedTokens, ResolveUsageTotal(activeUsage));
            }

            // Include draft input so the meter moves while typing, not only after send.
            try
            {
                if (ChatInputTextBox != null && !string.IsNullOrEmpty(ChatInputTextBox.Text))
                {
                    totalChars += ChatInputTextBox.Text.Length;
                }
            }
            catch
            {
                // Input box may not be ready during init; estimate stays history-only.
            }

            foreach (var att in PendingAttachments)
            {
                totalImages++;
            }

            int estimatedTokens = ModelContextSpecs.EstimateTokens(totalChars, totalImages);
            // Provider-reported total already includes system/tools/history and is
            // authoritative when present. Char estimate is only a fallback.
            int usedTokens = Math.Max(estimatedTokens, reportedTokens);

            if (maxTokens > 0)
            {
                double percentage = Math.Min(100.0, (usedTokens * 100.0) / maxTokens);

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

                ContextUsedLabel.Text = $"{ModelContextSpecs.FormatTokens(usedTokens)} / {displayLimit}";
                ContextPercentLabel.Text = $"{percentage:0.#}%";
                ContextPercentLabel.Foreground = percentage > 70 ? brush : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"];
            }
            else
            {
                ContextProgressRing.Value = 0;
                ContextProgressBar.Visibility = Visibility.Collapsed;
                ContextUsedLabel.Text = $"{ModelContextSpecs.FormatTokens(usedTokens)} tokens";
                ContextPercentLabel.Text = "Used";
                ContextPercentLabel.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"];
            }
        }

        private void OnChatInputTyping(object sender, TextChangedEventArgs e)
        {
            UpdateContextUsage();
            if (string.IsNullOrEmpty(ChatInputTextBox.Text))
            {
                _typingDebounce?.Stop();
                if (_typingPushed)
                {
                    _typingPushed = false;
                    PushSafelyAsync(_apiService?.PushVtsActivityAsync(false));
                }
                return;
            }
            _typingDebounce?.Stop();
            _typingDebounce?.Start();
        }

        private static async void PushSafelyAsync(Task? task)
        {
            if (task == null) return;
            try
            {
                await task;
            }
            catch
            {
                // Fire-and-forget VTS activity push should never crash the UI
            }
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

        private readonly List<AgentActivityModel> _pendingAgentActivities = new();
        private DispatcherQueueTimer? _activityFlushTimer;

        private void OnAgentActivityReceived(object? sender, AgentActivityModel activity)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                EnqueueAgentActivity(activity);
                return;
            }

            DispatcherQueue.TryEnqueue(() => EnqueueAgentActivity(activity));
        }

        private void EnqueueAgentActivity(AgentActivityModel activity)
        {
            _pendingAgentActivities.Add(activity);
            if (_activityFlushTimer is null)
            {
                _activityFlushTimer = DispatcherQueue.CreateTimer();
                _activityFlushTimer.Interval = TimeSpan.FromMilliseconds(80);
                _activityFlushTimer.Tick += OnActivityFlushTick;
            }

            if (!_activityFlushTimer.IsRunning)
            {
                _activityFlushTimer.Start();
            }
        }

        private void OnActivityFlushTick(Microsoft.UI.Dispatching.DispatcherQueueTimer? timer, object? _)
        {
            FlushAgentActivities();
        }

        private void FlushAgentActivities()
        {
            _activityFlushTimer?.Stop();
            if (_pendingAgentActivities.Count == 0)
            {
                return;
            }

            // Drain to a snapshot so re-entrant activity events during Apply
            // cannot mutate the collection mid-flush.
            var batch = _pendingAgentActivities.ToArray();
            _pendingAgentActivities.Clear();
            foreach (var activity in batch)
            {
                ApplyAgentActivity(activity);
            }

            AgentActivityGrouper.Reconcile(AgentActivities);
            ScrollChatToBottom();
        }

        private void ApplyAgentActivity(AgentActivityModel activity)
        {
            if (_agentController?.IsBusy != true
                || string.IsNullOrWhiteSpace(activity.ActivityId))
            {
                return;
            }

            if (_agentActivityById.TryGetValue(activity.ActivityId, out var existing))
            {
                existing.UpdateFrom(activity);
                TrackTerminalToolState(existing);
            }
            else
            {
                var item = new AgentActivityItem(activity);

                _agentActivityById.Add(item.ActivityId, item);
                AgentActivities.Add(item);
                TrackTerminalToolState(item);
            }
        }

        private void TrackTerminalToolState(AgentActivityItem item)
        {
            if (item.Kind == RuAgent.Protocol.Harness.AgentActivityKind.Thinking)
            {
                return;
            }

            if (item.State == RuAgent.Protocol.Harness.AgentActivityState.Started)
            {
                _lastTerminalToolLabel = item.DisplayText;
                return;
            }

            if (item.State is RuAgent.Protocol.Harness.AgentActivityState.Completed
                or RuAgent.Protocol.Harness.AgentActivityState.Failed)
            {
                CompleteTerminalBlock(item.DisplayText, failed: item.State == RuAgent.Protocol.Harness.AgentActivityState.Failed);
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
            if (item.IsFailure && HasRenderedAgentReply())
            {
                // A transient assistant reply may already be visible when the
                // history/outbox finalization reports a late failure. The reply
                // is the user-visible result of this turn; do not add noise
                // after it.
                return;
            }

            if (!item.IsFailure)
            {
                // Success and cancelled runs keep the transcript clean; the
                // assistant reply itself is the outcome the user reads.
                return;
            }

            var content = new System.Text.StringBuilder();
            content.Append("**").Append(item.DisplayText).Append("**");
            if (!string.IsNullOrWhiteSpace(item.MetadataText))
            {
                content.Append('\n').Append(item.MetadataText);
            }
            if (_agentController.CanContinue)
            {
                content.Append("\nType **Continue** to resume.");
            }

            Messages.Add(new ChatMessageItem
            {
                Role = "assistant",
                Content = content.ToString(),
                IsError = true,
            });
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
                ChatMessageItem? capturedAssistantMessage;
                string? capturedRunId;
                lock (_accumulatorLock)
                {
                    capturedAssistantMessage = _activeAgentAssistantMessage;
                    capturedRunId = _activeAgentRunId;
                }
                if (runForVoice != null && capturedAssistantMessage != null && string.Equals(capturedRunId ?? runForVoice.RunId, runForVoice.RunId, StringComparison.Ordinal))
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

            ChatMessageItem? activeAssistantMessage;
            string? activeRunId;
            lock (_accumulatorLock)
            {
                activeAssistantMessage = _activeAgentAssistantMessage;
                activeRunId = _activeAgentRunId;
            }
            if (activeAssistantMessage is null)
            {
                return;
            }

            activeRunId ??= run.RunId;
            lock (_accumulatorLock)
            {
                _activeAgentRunId = activeRunId;
            }
            if (!string.Equals(activeRunId, run.RunId, StringComparison.Ordinal))
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
                string? currentRunId;
                lock (_accumulatorLock)
                {
                    currentRunId = _activeAgentRunId;
                }
                if (currentRunId != null && run.RunId == currentRunId)
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
            _replyRenderScheduler.Schedule();
        }

        private void OnAgentUsageReceived(object? sender, AgentUsageReceivedEventArgs usage)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnAgentUsageReceived(sender, usage));
                return;
            }

            if (_agentController?.CurrentRun is not { } run
                || !string.Equals(run.RunId, usage.RunId, StringComparison.Ordinal)
                || !string.Equals(run.SessionId, usage.SessionId, StringComparison.Ordinal))
            {
                return;
            }

            _activeAgentUsage = usage.Usage;
            if (_activeAgentAssistantMessage is not null)
            {
                _activeAgentAssistantMessage.Usage = usage.Usage;
            }
            else
            {
                // The turn already completed and history was reloaded before the
                // usage payload arrived. Patch the last assistant bubble that was
                // rendered without usage so the footer does not go missing.
                for (int index = Messages.Count - 1; index >= 0; index--)
                {
                    var item = Messages[index];
                    if (string.Equals(item.Role, "assistant", StringComparison.Ordinal)
                        && item.Usage is null)
                    {
                        item.Usage = usage.Usage;
                        break;
                    }
                }
            }
            UpdateContextUsage();
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

            _activeAgentAssistantMessage.Content = snapshot.Text;
            _activeAgentAssistantMessage.IsStreaming = true;
            ScrollChatToBottom();
            UpdateContextUsage();
        }

        private void ClearAgentActivities()
        {
            StopAllTextSweeps();
            _activityFlushTimer?.Stop();
            _pendingAgentActivities.Clear();
            _agentActivityById.Clear();
            AgentActivities.Clear();
        }

        private void DetachAgentActivitiesFromMessages()
        {
            foreach (var message in Messages)
            {
                if (ReferenceEquals(message.AgentActivities, AgentActivities))
                {
                    message.AgentActivities = null;
                }
            }
        }

        private void StartNewAgentActivityRun()
        {
            foreach (var message in Messages)
            {
                if (ReferenceEquals(message.AgentActivities, AgentActivities))
                {
                    message.AgentActivities = AgentActivities.Count > 0
                        ? new ObservableCollection<AgentActivityItem>(AgentActivities)
                        : null;
                }
            }
            ClearAgentActivities();
            ClearTerminalBlocks();
        }

        // ── Terminal output panel ──────────────────────────────
        private const int MaximumTerminalBlocks = 5;
        private string? _lastTerminalToolLabel;
        private bool _terminalCollapsed;

        public ObservableCollection<TerminalOutputBlock> TerminalBlocks { get; } = new();

        private void OnAgentToolOutputReceived(object? sender, AgentToolOutputModel output)
        {
            if (DispatcherQueue.HasThreadAccess)
            {
                ApplyAgentToolOutput(output);
                return;
            }

            DispatcherQueue.TryEnqueue(() => ApplyAgentToolOutput(output));
        }

        private void ApplyAgentToolOutput(AgentToolOutputModel output)
        {
            if (_agentController?.IsBusy != true)
            {
                return;
            }

            var block = TerminalBlocks.FirstOrDefault(candidate =>
                string.Equals(candidate.ExecutionId, output.ExecutionId, StringComparison.Ordinal));
            if (block is null)
            {
                block = new TerminalOutputBlock(
                    output.ExecutionId,
                    string.IsNullOrWhiteSpace(_lastTerminalToolLabel)
                        ? "Command output"
                        : _lastTerminalToolLabel);
                TerminalBlocks.Add(block);
                while (TerminalBlocks.Count > MaximumTerminalBlocks)
                {
                    TerminalBlocks.RemoveAt(0);
                }
            }

            block.AppendText(output.Text);
            _terminalCollapsed = false;
            TerminalPanel.Visibility = Visibility.Visible;
            TerminalScrollHost.ChangeView(null, double.MaxValue, null);
        }

        private void CompleteTerminalBlock(string label, bool failed)
        {
            TerminalOutputBlock? block = null;
            foreach (var candidate in TerminalBlocks)
            {
                if (!candidate.IsCompleted
                    && string.Equals(candidate.Title, label, StringComparison.Ordinal))
                {
                    block = candidate;
                }
            }

            block ??= TerminalBlocks.LastOrDefault(candidate => !candidate.IsCompleted);
            block?.Complete(failed);
        }

        private void ClearTerminalBlocks()
        {
            TerminalBlocks.Clear();
            _lastTerminalToolLabel = null;
            TerminalPanel.Visibility = Visibility.Collapsed;
        }

        private void OnTerminalClearClick(object sender, RoutedEventArgs e) => ClearTerminalBlocks();

        private void OnTerminalCollapseClick(object sender, RoutedEventArgs e)
        {
            _terminalCollapsed = !_terminalCollapsed;
            TerminalScrollHost.Visibility = _terminalCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ((Button)sender).Content = _terminalCollapsed ? "Show" : "Hide";
        }

        // ── Activity text wave animation ──────────────────────────────
        private readonly HashSet<TextBlock> _activeWaveTextBlocks = new();
        private DispatcherQueueTimer? _waveTimer;
        private readonly System.Diagnostics.Stopwatch _waveStopwatch = new();

        private void OnActivityItemLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            grid.DataContextChanged -= OnActivityItemDataContextChanged;
            grid.DataContextChanged += OnActivityItemDataContextChanged;
            AttachActivityItem(grid);
        }

        private void OnActivityItemDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Grid grid)
            {
                AttachActivityItem(grid);
            }
        }

        private void OnActivityItemUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            grid.DataContextChanged -= OnActivityItemDataContextChanged;
            DetachActivityItem(grid);
        }

        private void AttachActivityItem(Grid grid)
        {
            DetachActivityItem(grid, clearEntrance: false);

            if (grid.DataContext is not AgentActivityItem item)
                return;

            if (grid.Tag == null)
            {
                grid.Opacity = 0;
                var translate = new TranslateTransform { Y = 4 };
                grid.RenderTransform = translate;

                var fadeIn = new DoubleAnimation
                {
                    From = 0.0,
                    To = 1.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(fadeIn, grid);
                Storyboard.SetTargetProperty(fadeIn, "Opacity");

                var slideIn = new DoubleAnimation
                {
                    From = 4.0,
                    To = 0.0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(slideIn, translate);
                Storyboard.SetTargetProperty(slideIn, "Y");

                var entranceSb = new Storyboard();
                entranceSb.Children.Add(fadeIn);
                entranceSb.Children.Add(slideIn);
                entranceSb.Begin();
            }

            TextBlock? displayText = null;
            foreach (var child in grid.Children)
            {
                if (child is Grid header)
                {
                    foreach (var headerChild in header.Children)
                    {
                        // The activity row is flat: column 0 is the display
                        // label, column 1 the metadata line. Sweep the label.
                        if (headerChild is TextBlock tb && Grid.GetColumn(tb) == 0)
                        {
                            displayText = tb;
                            break;
                        }
                    }
                }

                if (displayText != null)
                {
                    break;
                }
            }
            if (displayText == null) return;

            ApplyActivityTextStyle(displayText, item);

            PropertyChangedEventHandler handler = (s, args) =>
            {
                if (string.IsNullOrEmpty(args.PropertyName) ||
                    args.PropertyName == nameof(AgentActivityItem.State) ||
                    args.PropertyName == nameof(AgentActivityItem.IsRunning) ||
                    args.PropertyName == nameof(AgentActivityItem.IsCompleted) ||
                    args.PropertyName == nameof(AgentActivityItem.IsFailed))
                {
                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (s is AgentActivityItem ai && ReferenceEquals(grid.DataContext, ai))
                        {
                            ApplyActivityTextStyle(displayText, ai);
                        }
                    });
                }
            };
            item.PropertyChanged += handler;
            grid.Tag = (handler, displayText, item);
        }

        private void DetachActivityItem(Grid grid, bool clearEntrance = true)
        {
            if (grid.Tag is ValueTuple<PropertyChangedEventHandler, TextBlock, AgentActivityItem> tuple)
            {
                tuple.Item3.PropertyChanged -= tuple.Item1;
                StopTextSweep(tuple.Item2);
            }
            if (clearEntrance)
            {
                grid.Tag = null;
            }
        }

        private void ApplyActivityTextStyle(TextBlock textBlock, AgentActivityItem item)
        {
            if (item.IsRunning)
            {
                StartTextSweep(textBlock);
            }
            else
            {
                StopTextSweep(textBlock);
                var targetBrush = item.IsFailed
                    ? (SolidColorBrush)Application.Current.Resources["RuDangerBrush"]
                    : (SolidColorBrush)Application.Current.Resources["RuInkBrush"];

                textBlock.Foreground = targetBrush;
            }
        }

        private void StartTextSweep(TextBlock textBlock)
        {
            if (!_activeWaveTextBlocks.Add(textBlock))
                return;

            if (_waveTimer == null)
            {
                _waveTimer = DispatcherQueue.CreateTimer();
                _waveTimer.Interval = Services.UiTiming.WaveFrame; // ~20 FPS
                _waveTimer.Tick += OnWaveTimerTick;
            }

            if (!_waveTimer.IsRunning)
            {
                _waveStopwatch.Restart();
                _waveTimer.Start();
            }

            UpdateWaveTextBlock(textBlock, 0.0);
        }

        private void StopTextSweep(TextBlock textBlock)
        {
            if (_activeWaveTextBlocks.Remove(textBlock))
            {
                if (_activeWaveTextBlocks.Count == 0)
                {
                    _waveTimer?.Stop();
                    _waveStopwatch.Reset();
                }
            }
        }

        private void StopAllTextSweeps()
        {
            _activeWaveTextBlocks.Clear();
            _waveTimer?.Stop();
            _waveStopwatch.Reset();
        }

        private void OnWaveTimerTick(DispatcherQueueTimer sender, object args)
        {
            if (_activeWaveTextBlocks.Count == 0)
            {
                sender.Stop();
                _waveStopwatch.Reset();
                return;
            }

            double elapsed = _waveStopwatch.Elapsed.TotalSeconds;
            double phase = (elapsed / 1.25) % 1.0;

            List<TextBlock>? deadBlocks = null;
            foreach (var tb in _activeWaveTextBlocks)
            {
                if (tb.XamlRoot == null)
                {
                    deadBlocks ??= new List<TextBlock>();
                    deadBlocks.Add(tb);
                    continue;
                }
                UpdateWaveTextBlock(tb, phase);
            }

            if (deadBlocks != null)
            {
                foreach (var dead in deadBlocks)
                {
                    _activeWaveTextBlocks.Remove(dead);
                }
                if (_activeWaveTextBlocks.Count == 0)
                {
                    sender.Stop();
                    _waveStopwatch.Reset();
                }
            }
        }

        private static void UpdateWaveTextBlock(TextBlock tb, double phase)
        {
            var mutedBrush = Application.Current.Resources["RuMutedBrush"] as SolidColorBrush;
            var baseColor = mutedBrush?.Color ?? Windows.UI.Color.FromArgb(255, 148, 163, 184);

            // Subtle Cyan-Violet Aurora beam center (-0.35 to 1.35)
            double center = -0.35 + 1.70 * phase;

            // Cache brush per TextBlock to avoid allocation churn
            if (tb.Tag is not LinearGradientBrush brush)
            {
                brush = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0.5),
                    EndPoint = new Point(1, 0.5),
                    GradientStops =
                    {
                        new GradientStop { Color = baseColor, Offset = 0.00 },
                        new GradientStop { Color = baseColor, Offset = 0.25 },
                        new GradientStop { Color = baseColor, Offset = 0.50 },
                        new GradientStop { Color = baseColor, Offset = 0.75 },
                        new GradientStop { Color = baseColor, Offset = 1.00 },
                    }
                };
                tb.Tag = brush;
            }

            // Mutate stops in place
            var stops = brush.GradientStops;
            stops[0].Color = SampleAuroraColor(0.00, center, baseColor);
            stops[1].Color = SampleAuroraColor(0.25, center, baseColor);
            stops[2].Color = SampleAuroraColor(0.50, center, baseColor);
            stops[3].Color = SampleAuroraColor(0.75, center, baseColor);
            stops[4].Color = SampleAuroraColor(1.00, center, baseColor);

            tb.Foreground = brush;
        }

        private static Windows.UI.Color SampleAuroraColor(double u, double center, Windows.UI.Color baseColor) =>
            Services.AgentActivityPalette.SampleAuroraColor(u, center, baseColor);

        private static Windows.UI.Color LerpColor(Windows.UI.Color from, Windows.UI.Color to, double progress) =>
            Services.AgentActivityPalette.LerpColor(from, to, progress);

        private void ShowAgentInlineStatus(string status)
        {
            _agentActivityVisible = false;
            AgentStatusText.Text = status;
            AgentStatusText.Visibility = string.IsNullOrWhiteSpace(status)
                ? Visibility.Collapsed
                : Visibility.Visible;
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
                "Start voice conversation");
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
                AutomationProperties.SetName(SendButton, "Stop Coding Agent");
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
                AutomationProperties.SetName(SendButton, "Send message");
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
            AgentApprovalPanel.Visibility = Visibility.Collapsed;
                UpdateSendButtonMode(isAgentRunning: false);
                return;
            }

            bool keepTerminalFailureVisible = AgentFailurePresentation.ShouldKeepVisible(
                _agentController.CurrentRun?.Status,
                ResolveFailureReason(_agentController));

            if (_agentController.IsBusy != true
                && !_agentController.CanCancel
                && !_agentController.HasPendingApproval
                && !_agentController.NeedsWorkspaceTrust
                && !_agentController.CanContinue
                && !keepTerminalFailureVisible)
            {
                _agentActivityVisible = false;
                AgentInlinePanel.Visibility = Visibility.Collapsed;
                AgentStatusText.Text = string.Empty;
            AgentStopButton.Visibility = Visibility.Collapsed;
            AgentApprovalPanel.Visibility = Visibility.Collapsed;
                UpdateSendButtonMode(isAgentRunning: false);
                return;
            }

            bool isRunning = _agentController.CanCancel || IsActiveAgentRunPending();
            UpdateSendButtonMode(isAgentRunning: isRunning);

            string safeStatus = AgentFailurePresentation.Render(ResolveFailureReason(_agentController));
            if (keepTerminalFailureVisible && !isRunning)
            {
                // Terminal failure copy now lives in the transcript as an
                // error message; repeating it above the composer would show
                // the same failure twice.
                safeStatus = string.Empty;
            }
            AgentStatusText.Text = safeStatus;
            AgentStatusText.Visibility = string.IsNullOrWhiteSpace(safeStatus)
                ? Visibility.Collapsed
                : Visibility.Visible;
            AgentStopButton.Visibility = Visibility.Collapsed;

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
                // Approval arrives via SSE while the user may be scrolled up;
                // bring the Approve/Deny panel into view.
                ScrollChatToBottom();
            }

            bool hasInlineAction = approvalPending
                || _agentController.CanContinue
                || keepTerminalFailureVisible;
            AgentInlinePanel.Visibility = hasInlineAction ? Visibility.Visible : Visibility.Collapsed;

            bool actionsEnabled = !_agentActionPending;
            AgentApproveButton.IsEnabled = actionsEnabled;
            AgentDenyButton.IsEnabled = actionsEnabled;
            AgentStopButton.IsEnabled = actionsEnabled;
        }

        private static string ResolveFailureReason(AgentRunController controller)
        {
            // The SSE final_snapshot only carries the bare run status ("failed"),
            // which tells the user nothing. Prefer the durable terminal error
            // code (repeated_tool_call, provider_rate_limited, ...) whenever the
            // stream text is just a bare status word.
            string streamText = controller.StatusText ?? string.Empty;
            string? terminalCode = string.Equals(controller.CurrentRun?.Status, "failed", StringComparison.Ordinal)
                ? controller.CurrentRun?.TerminalErrorCode
                : null;
            if (!string.IsNullOrWhiteSpace(terminalCode) && IsBareRunStatusWord(streamText))
            {
                return terminalCode!;
            }

            return streamText;
        }

        private static bool IsBareRunStatusWord(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            return text.Trim() is string trimmed
                && (string.Equals(trimmed, "failed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "completed", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "cancelled", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "interrupted", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(trimmed, "Run ended.", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Server-selected pending approval reason drives the bounded copy.
        /// The Host is the only authority, so we never echo the raw argv, script,
        /// working directory, or secrets. Reason codes come from the broker
        /// (external_directory_required, terminal_delete_requires_approval,
        /// terminal_external_mutation_requires_approval, terminal_timeout_requires_approval,
        /// web_approval_required, terminal_unknown_executable);
        /// unknown values stay generic.
        /// </summary>
        private static string RenderApprovalSummary(string? summary)
        {
            string trimmed = BoundedDisplay(summary ?? string.Empty, 64);
            if (string.Equals(trimmed, "external_directory_required", StringComparison.Ordinal))
            {
                return "Ru needs to access a directory outside the project in this chat until the app closes.";
            }
            if (string.Equals(trimmed, "terminal_delete_requires_approval", StringComparison.Ordinal))
            {
                return "This terminal command will delete files.";
            }
            if (string.Equals(trimmed, "terminal_external_mutation_requires_approval", StringComparison.Ordinal))
            {
                return "This terminal command changes something outside the workspace (network, install).";
            }
            if (string.Equals(trimmed, "terminal_timeout_requires_approval", StringComparison.Ordinal))
            {
                return "This terminal command runs longer than the standard window.";
            }
            if (string.Equals(trimmed, "web_approval_required", StringComparison.Ordinal))
            {
                return "Ru needs approval to fetch this web content.";
            }
            if (string.Equals(trimmed, "terminal_unknown_executable", StringComparison.Ordinal))
            {
                return "Ru has not recognized the executable for this terminal command.";
            }
            if (string.IsNullOrEmpty(trimmed))
            {
                return "Agent is requesting an action that requires approval.";
            }
            return trimmed;
        }

        private static string BoundedDisplay(string value, int maxLength)
        {
            string normalized = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "…";
        }

        private bool IsActiveAgentRunPending()
        {
            // Synchronous send guard: _isSendingUserTurn covers the race between
            // tapping Send and the first await (session ensure + create run),
            // during which CanCancel/HasPendingApproval are still false and a
            // second Enter would otherwise create a duplicate user bubble + run.
            // Entry points (Enter key, Send click) use this; SendAgentTurnAsync
            // itself must use IsControllerRunPending or it would block its own
            // caller, which already holds the flag.
            return _isSendingUserTurn || IsControllerRunPending();
        }

        private bool IsControllerRunPending()
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

            var monitor = _agentMonitorCts;
            _agentMonitorCts = null;
            monitor?.Cancel();
            monitor?.Dispose();
            _agentActivityVisible = false;
            _agentActionPending = false;
            _isSendingUserTurn = false;
            _activeAgentUsage = null;
            AbandonActiveAgentReply(notifyVoice: true);

            UpdateAgentComposerVisuals();
            AgentInlinePanel.Visibility = Visibility.Collapsed;
            AgentApprovalPanel.Visibility = Visibility.Collapsed;
            AgentStopButton.Visibility = Visibility.Collapsed;
            AgentStatusText.Text = string.Empty;
            AgentStatusText.Visibility = Visibility.Collapsed;
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
                e.DragUIOverride.Caption = "Drop to attach files / images";
                e.DragUIOverride.IsContentVisible = true;
            }
        }

        private async void OnInputDrop(object sender, DragEventArgs e)
        {
            try
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatView] Error dropping files: {ex.Message}");
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
                        ShowAgentInlineStatus("Maximum 4 images per turn; please remove some images before adding.");
                        return;
                    }

                    var streamRef = await package.GetBitmapAsync();
                    using var sourceStream = await streamRef.OpenReadAsync();
                    if (sourceStream.Size > (ulong)MaxImageAttachmentBytes)
                    {
                        ShowAgentInlineStatus("Clipboard image exceeds the allowed 10MB limit.");
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
                    ShowAgentInlineStatus($"File '{file.Name}' is not supported. Only images and text/code files are accepted.");
                    return;
                }

                if (isImage)
                {
                    if (PendingAttachments.Count(static a => a.IsImage) >= 4)
                    {
                        ShowAgentInlineStatus("Maximum 4 images per turn; please remove some images before adding.");
                        return;
                    }

                    if (props.Size > MaxImageAttachmentBytes)
                    {
                        ShowAgentInlineStatus($"Image '{file.Name}' exceeds the allowed 10MB limit.");
                        return;
                    }

                    string inputMime = Services.ChatAttachmentPolicy.MimeForExtension(file.Name);

                    using var sourceStream = await file.OpenReadAsync();
                    if (sourceStream.Size > (ulong)MaxImageAttachmentBytes)
                    {
                        ShowAgentInlineStatus($"Image '{file.Name}' exceeds the allowed 10MB limit.");
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
                        ShowAgentInlineStatus($"File '{file.Name}' exceeds the allowed 5MB limit.");
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
                                ShowAgentInlineStatus($"File '{file.Name}' exceeds the allowed 5MB limit.");
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

        /// <summary>
        /// A bare typed "Continue" (no attachments) resumes a resumable run
        /// instead of starting a new turn. Case-insensitive; surrounding
        /// whitespace is ignored.
        /// </summary>
        public static bool IsContinueResumeCommand(string text, IReadOnlyCollection<ImageAttachmentItem> attachments)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(attachments);
            return attachments.Count == 0
                && string.Equals(text.Trim(), "Continue", StringComparison.OrdinalIgnoreCase);
        }

        private async Task SendCurrentMessageAsync()
        {
            if (_isSendingUserTurn)
            {
                return;
            }

            try
            {
                string text = ChatInputTextBox.Text.Trim();
                var attachments = PendingAttachments.ToList();

                if (string.IsNullOrEmpty(text) && attachments.Count == 0) return;

                if (IsContinueResumeCommand(text, attachments) && _agentController?.CanContinue == true)
                {
                    // Typed "Continue" resumes a resumable run instead of
                    // starting a new turn; the failure message points here.
                    ChatInputTextBox.Text = string.Empty;
                    await RunAgentActionAsync(
                        () => _agentController?.ContinueAsync() ?? Task.CompletedTask,
                        "Resuming Coding Agent…");
                    return;
                }

                _isSendingUserTurn = true;
                UpdateSendButtonMode(isAgentRunning: true);

                int imageCount = attachments.Count(static attachment => attachment.IsImage);
                if (imageCount > 4)
                {
                    ShowAgentInlineStatus("Maximum 4 images per turn; please remove some images before sending.");
                    return;
                }

                ChatInputTextBox.Text = string.Empty;
                PendingAttachments.Clear();
                AttachmentPreviewContainer.Visibility = Visibility.Collapsed;

                if (_typingPushed)
                {
                    _typingPushed = false;
                    PushSafelyAsync(_apiService?.PushVtsActivityAsync(false));
                }

                var textAttachments = attachments.Where(a => !a.IsImage && !string.IsNullOrWhiteSpace(a.TextContent)).ToList();
                string effectivePrompt = text;
                if (textAttachments.Count > 0)
                {
                    var sb = new System.Text.StringBuilder();
                    foreach (var doc in textAttachments)
                    {
                        string lang = doc.FileExtension.TrimStart('.');
                        sb.AppendLine($"[Attached file: {doc.FileName} ({doc.FormattedSize})]");
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
            finally
            {
                _isSendingUserTurn = false;
                UpdateAgentInlineState();
            }
        }

        private async Task SendAgentTurnAsync(            string text,
            AgentTurnSource source,
            CancellationToken cancellationToken,
            bool allowSessionRecovery = true,
            List<ImageAttachmentItem>? attachments = null,
            string? displayUserText = null,
            bool addUserMessage = true)
        {
            if (allowSessionRecovery)
            {
                _agentSessionRecovery.Reset();
            }

            if (_agentController is null)
            {
                throw new InvalidOperationException("Agent controller is unavailable.");
            }

            if (IsControllerRunPending())
            {
                ShowAgentInlineStatus("Coding Agent is already running. Please complete or stop the current turn first.");
                if (source == AgentTurnSource.Voice)
                {
                    throw new InvalidOperationException("Coding Agent is already processing a turn.");
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
                    null,
                    Guid.NewGuid().ToString("N"),
                    imagePayloads);

                _activeWorkspaceId = null;

                StartNewAgentActivityRun();
                BeginAgentReply(source, text, attachments, displayUserText, addUserMessage);
                ScrollChatToBottom();
                ShowAgentInlineStatus("Preparing action…");
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
                AbandonActiveAgentReply(notifyVoice: false, removeUserMessage: true);
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
                await CompleteActiveAgentTurnAsync(cancellationToken);
            }
            else
            {
                StartAgentMonitor();
            }
            UpdateAgentInlineState();
        }

        private void BeginAgentReply(AgentTurnSource source, string text, List<ImageAttachmentItem>? attachments = null, string? displayUserText = null, bool addUserMessage = true)
        {
            _activeAgentTurnSource = source;
            _activeAgentRunId = null;
            _activeAgentReplyReceived = false;
            _activeAgentReplyCompleted = false;
            _activeAgentUsage = null;
            _agentDeltaKeys.Clear();
            _replyRenderScheduler.Invalidate();

            if (addUserMessage)
            {
                Messages.Add(new ChatMessageItem
                {
                    Role = "user",
                    Content = !string.IsNullOrEmpty(displayUserText) ? displayUserText : text,
                    SessionId = _activeSessionId,
                    Attachments = attachments != null && attachments.Count > 0
                        ? new ObservableCollection<ImageAttachmentItem>(attachments)
                        : new ObservableCollection<ImageAttachmentItem>()
                });
            }

            _activeAgentAssistantMessage = new ChatMessageItem
            {
                Role = "assistant",
                Content = string.Empty,
                IsStreaming = true,
                SessionId = _activeSessionId,
            };
            _activeAgentAssistantMessage.AgentActivities = AgentActivities;
            Messages.Add(_activeAgentAssistantMessage);

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
            if (latestAssistant is null
                && !_activeAgentReplyReceived
                && placeholder is not null
                && string.IsNullOrWhiteSpace(placeholder.Content)
                && Messages.Contains(placeholder))
            {
                Messages.Remove(placeholder);
            }
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
            AgentReplySnapshot? pending = _replyRenderScheduler.Flush();
            if (pending is not null && _activeAgentAssistantMessage is not null)
            {
                _activeAgentAssistantMessage.Content = pending.Text;
            }

            bool hasReply = HasRenderedAgentReply();
            _replyRenderScheduler.Invalidate();
            if (_activeAgentAssistantMessage is not null)
            {
                if (!Messages.Contains(_activeAgentAssistantMessage))
                {
                    Messages.Add(_activeAgentAssistantMessage);
                }
                if (!hasReply)
                {
                    _activeAgentAssistantMessage.Content = "Coding Agent could not complete this turn. Please try again later.";
                }
                _activeAgentAssistantMessage.IsStreaming = false;
                _activeAgentAssistantMessage.IsError = !hasReply;
            }

            if (_activeAgentTurnSource == AgentTurnSource.Voice)
            {
                if (hasReply)
                {
                    _voiceConversation?.NotifyReplyCompleted();
                }
                else
                {
                    _voicePlayback?.FailReplyStream();
                    _voiceConversation?.NotifyReplyError(exception);
                }
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _agentDeltaKeys.Clear();
            _activeAgentTurnSource = AgentTurnSource.Typed;
        }

        private void AbandonActiveAgentReply(bool notifyVoice, bool removeUserMessage = false)
        {
            _replyRenderScheduler.Invalidate();
            _voicePlayback?.StopVoice();
            if (notifyVoice && _activeAgentTurnSource == AgentTurnSource.Voice)
            {
                _voiceConversation?.NotifyReplyError(
                    new OperationCanceledException("Agent turn was replaced or cancelled."));
            }

            if (removeUserMessage && Messages.Count > 0)
            {
                var last = Messages[^1];
                if (string.Equals(last.Role, "user", StringComparison.Ordinal))
                {
                    Messages.RemoveAt(Messages.Count - 1);
                }
            }

            if (_activeAgentAssistantMessage is not null)
            {
                var abandonedMessage = _activeAgentAssistantMessage;
                abandonedMessage.IsStreaming = false;
                if (string.IsNullOrWhiteSpace(abandonedMessage.Content)
                    && Messages.Contains(abandonedMessage))
                {
                    Messages.Remove(abandonedMessage);
                }
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _activeAgentReplyReceived = false;
            _activeAgentReplyCompleted = false;
            _agentDeltaKeys.Clear();
            _activeAgentTurnSource = AgentTurnSource.Typed;
        }

        private async Task<SessionInfo> CreateValidatedAgentSessionAsync(CancellationToken cancellationToken)
        {
            if (_apiService is null)
            {
                throw new InvalidOperationException("Backend session is unavailable.");
            }

            SessionInfo? created = await _apiService.CreateSessionAsync();
            cancellationToken.ThrowIfCancellationRequested();
            string? sessionId = created?.Id?.Trim();
            if (created is null || !AgentSessionSelection.IsValid(sessionId))
            {
                throw new AgentApiException(
                    "session_create_failed",
                    "Could not create a session for Coding Agent.");
            }

            return created;
        }

        private async Task RecoverAgentSessionAsync(CancellationToken cancellationToken)
        {
            SessionInfo created = await CreateValidatedAgentSessionAsync(cancellationToken);
            string sessionId = created.Id.Trim();

            _activeSessionId = sessionId;
            ClearAgentActivities();

            DetachAgentActivitiesFromMessages();
            Messages.Clear();
            UpdateViewMode();
            SessionOpened?.Invoke(
                this,
                new SessionOpenedEventArgs(
                    sessionId,
                    created.Title ?? "New Conversation"));
        }

        private async Task EnsureAgentSessionAsync(CancellationToken cancellationToken)
        {
            if (AgentSessionSelection.IsValid(_activeSessionId))
            {
                return;
            }

            SessionInfo created = await CreateValidatedAgentSessionAsync(cancellationToken);
            string validSessionId = created.Id.Trim();
            _activeSessionId = validSessionId;
            SessionOpened?.Invoke(
                this,
                new SessionOpenedEventArgs(
                    validSessionId,
                    created.Title ?? "New Conversation"));
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

        private async void OnImageAttachmentClick(object sender, RoutedEventArgs e)
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
                    Title = string.IsNullOrWhiteSpace(item.FileName) ? "Attached image" : item.FileName,
                    Content = new ScrollViewer
                    {
                        Content = image,
                        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    },
                    CloseButtonText = "Close",
                    XamlRoot = XamlRoot,
                };
                await dialog.ShowAsync();
            }
        }

        private static bool IsTerminalStatus(string? status) => status is
            "completed" or "failed" or "cancelled" or "interrupted";

        private void ShowAgentError(Exception exception)
        {
            if (HasRenderedAgentReply())
            {
                return;
            }

            string status = exception switch
            {
                AgentApiException api => api.ErrorCode switch
                {
                    "invalid_workspace" => "Invalid or non-existent workspace folder.",
                    "workspace_busy" => "Workspace is currently in use by another Agent session.",
                    "preset_mismatch" or "preset_version_conflict" => "Permission preset has changed. Please select a preset and try again.",
                    "session_create_failed" => "Failed to create a session for Coding Agent. Check backend and try again.",
                    "session_not_found" or "invalid_session_id" => "Chat session is no longer valid. Send a message to create a new session.",
                    "unauthorized" => "Agent session credentials are no longer valid. Please restart backend.",
                    "agent_store_unavailable" or "harness_store_unavailable" => "Agent database is temporarily unavailable. Check permissions or restart.",
                    _ => !string.IsNullOrWhiteSpace(api.Message) && !string.Equals(api.Message, api.ErrorCode, StringComparison.OrdinalIgnoreCase)
                        ? $"Coding Agent could not start ({api.ErrorCode}: {api.Message}). Open Tools → Activity Log to check."
                        : $"Coding Agent could not start ({api.ErrorCode}). Open Tools → Activity Log to check."
                },
                _ => $"Coding Agent could not start: {exception.Message}"
            };
            ShowAgentInlineStatus(status);
            AgentStopButton.Visibility = Visibility.Collapsed;
            AgentApprovalPanel.Visibility = Visibility.Collapsed;
        }

        private bool HasRenderedAgentReply() =>
            _activeAgentReplyReceived
            || (_activeAgentAssistantMessage is { Content: { } content }
                && !string.IsNullOrWhiteSpace(content));

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
                    await Task.Delay(Services.UiTiming.AgentRetryDelay, cancellationToken);
                    if (_agentController.CurrentRun is null)
                    {
                        return;
                    }

                    if (IsTerminalStatus(_agentController.CurrentRun.Status))
                    {
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

            string? streamedReply = _activeAgentAssistantMessage is { Content: { } content }
                ? StripDisplayMarkers(content)
                : null;
            var history = await _apiService.GetSessionMessagesAsync(_activeSessionId);
            if (history is null || cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var normalizedHistory = history
                .Select(item => (
                    item.Role,
                    Content: string.Equals(item.Role, "assistant", StringComparison.Ordinal)
                        ? StripDisplayMarkers(item.Content)
                        : item.Content))
                .ToList();
            string? latestAssistant = history
                .LastOrDefault(item => string.Equals(item.Role, "assistant", StringComparison.Ordinal))
                .Content;
            latestAssistant = StripDisplayMarkers(latestAssistant ?? string.Empty);
            bool preserveStreamedReply = ChatHistoryReloadPolicy.ShouldPreserveStreamedReply(
                normalizedHistory,
                streamedReply);

            void Apply()
            {
                _suppressContextUsageUpdate = true;
                try
                {
                    if (preserveStreamedReply
                        && _activeAgentAssistantMessage is { } streamedMessage
                        && !string.IsNullOrWhiteSpace(streamedReply)
                        && !ModelToolSelectionParser.LooksLikeToolSelection(streamedReply))
                    {
                        if (!Messages.Contains(streamedMessage))
                        {
                            Messages.Add(streamedMessage);
                        }

                        streamedMessage.Content = streamedReply;
                        streamedMessage.IsStreaming = false;
                        streamedMessage.Usage = _activeAgentUsage;
                        ScrollChatToBottom();
                        UpdateViewMode();
                        return;
                    }

                    var existingActivities = Messages
                        .Where(m => m.Role == "assistant")
                        .Select(m => m.AgentActivities != null && !ReferenceEquals(m.AgentActivities, AgentActivities)
                            ? m.AgentActivities
                            : (ReferenceEquals(m.AgentActivities, AgentActivities) && AgentActivities.Count > 0
                                ? new ObservableCollection<AgentActivityItem>(AgentActivities)
                                : null))
                        .ToList();

                    DetachAgentActivitiesFromMessages();
                    Messages.Clear();
                    int assistantCount = 0;
                    int lastAssistantIndex = history.FindLastIndex(
                        item => string.Equals(item.Role, "assistant", StringComparison.Ordinal));
                    for (int index = 0; index < history.Count; index++)
                    {
                        var (role, content) = history[index];
                        // A machine tool envelope must never render as chat text
                        // (e.g. a repeated call that failed the run). Drop it.
                        if (string.Equals(role, "assistant", StringComparison.Ordinal)
                            && ModelToolSelectionParser.LooksLikeToolSelection(StripDisplayMarkers(content)))
                        {
                            continue;
                        }

                        ObservableCollection<AgentActivityItem>? activities = null;
                        if (role == "assistant")
                        {
                            if (index == lastAssistantIndex)
                            {
                                activities = AgentActivities.Count > 0
                                    ? new ObservableCollection<AgentActivityItem>(AgentActivities)
                                    : (assistantCount < existingActivities.Count ? existingActivities[assistantCount] : null);
                            }
                            else if (assistantCount < existingActivities.Count)
                            {
                                activities = existingActivities[assistantCount];
                            }
                            assistantCount++;
                        }

                        Messages.Add(new ChatMessageItem
                        {
                            Role = role,
                            Content = role == "assistant" ? StripDisplayMarkers(content) : content,
                            Usage = index == lastAssistantIndex ? _activeAgentUsage : null,
                            AgentActivities = activities
                        });
                    }

                    if (history.Count > 0)
                    {
                        ScrollChatToBottom();
                    }

                    UpdateViewMode();
                }
                finally
                {
                    _suppressContextUsageUpdate = false;
                    UpdateContextUsage();
                }
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

            return preserveStreamedReply ? streamedReply : latestAssistant;
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
                throw new InvalidOperationException("Could not switch speech turn to UI thread.");
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

            double horizontalInset = RootGrid.ActualWidth < NarrowWidthThreshold
                ? NarrowHorizontalInset
                : RootGrid.ActualWidth < MediumWidthThreshold
                    ? MediumHorizontalInset
                    : WideHorizontalInset;
            double availableWidth = Math.Max(MinAvailableInputWidth, RootGrid.ActualWidth - horizontalInset);
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
            if (ChatScrollHost.ScrollableHeight > 0)
            {
                double verticalOffset = ChatScrollHost.VerticalOffset;
                double maxOffset = ChatScrollHost.ScrollableHeight;
                bool nearBottom = maxOffset - verticalOffset <= 100; // Within 100px of bottom
                if (nearBottom)
                {
                    ChatScrollHost.ChangeView(null, maxOffset, null, disableAnimation: true);
                }
                return;
            }
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RetryScrollAfterLayout);
        }

        private void RetryScrollAfterLayout()
        {
            if (ChatScrollHost.ScrollableHeight > 0)
            {
                double verticalOffset = ChatScrollHost.VerticalOffset;
                double maxOffset = ChatScrollHost.ScrollableHeight;
                bool nearBottom = maxOffset - verticalOffset <= 100;
                if (nearBottom)
                {
                    ChatScrollHost.ChangeView(null, maxOffset, null, disableAnimation: true);
                }
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
                    "Stopping Coding Agent…");
            }
            AbandonActiveAgentReply(notifyVoice: true);
            UpdateSendButtonMode(isAgentRunning: false);
        }

        private async void OnAgentStopClick(object sender, RoutedEventArgs e)
        {
            await StopActiveAgentAsync();
        }

        private async void OnAgentApproveClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.ApproveAsync() ?? Task.CompletedTask,
                "Submitting approval…");
        }

        private async void OnAgentDenyClick(object sender, RoutedEventArgs e)
        {
            await RunAgentActionAsync(
                () => _agentController?.DenyAsync() ?? Task.CompletedTask,
                "Denying approval…");
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
                _activeSessionTitle = string.IsNullOrWhiteSpace(title) ? "New Chat" : title;
                ResetAgentState();
                SessionOpened?.Invoke(this, new SessionOpenedEventArgs(sessionId, title));
                DetachAgentActivitiesFromMessages();
                Messages.Clear();
                var history = await _apiService.GetSessionMessagesAsync(_activeSessionId);
                if (history != null)
                {
                    foreach (var (role, content) in history)
                    {
                        if (string.Equals(role, "assistant", StringComparison.Ordinal)
                            && ModelToolSelectionParser.LooksLikeToolSelection(StripDisplayMarkers(content)))
                        {
                            continue;
                        }

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
                    Content = "**SESSION LOAD ERROR:** Could not load chat session. Please check backend and try again.",
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
                ChatInputTextBox.PlaceholderText = "Could not access microphone. Check microphone permissions.";
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
                throw new InvalidOperationException("Voice API is not ready.");
            }

            string? text = await _apiService.TranscribeAudioAsync(wavBytes, cancellationToken);
            if (text == null)
            {
                throw new InvalidOperationException("Speech recognition service is unavailable.");
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
                    VoiceConversationStatusText.Text = "Stopped";
                    VoiceConversationStatusText.Visibility = Visibility.Visible;
                    return;
                }

                if (status.State == VoiceConversationState.Error)
                {
                    SetVoiceButtonState(false);
                    ChatInputTextBox.PlaceholderText = status.Message
                        ?? "Voice conversation stopped.";
                    VoiceConversationStatusText.Text = status.Message ?? "Could not access microphone.";
                    VoiceConversationStatusText.Visibility = Visibility.Visible;
                    return;
                }

                SetVoiceButtonState(true);
                VoiceConversationStatusText.Text = status.State switch
                {
                    VoiceConversationState.Listening => "Listening...",
                    VoiceConversationState.Transcribing => "Processing...",
                    VoiceConversationState.Sending or VoiceConversationState.WaitingForReply => "Waiting for Ru to reply...",
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
                active ? "Stop voice conversation" : "Start voice conversation");
        }


        private void OnCopyMessageContentClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.Tag is ChatMessageItem msg && !string.IsNullOrEmpty(msg.Content))
            {
                try
                {
                    var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                    package.SetText(msg.Content);
                    Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ChatView] Error copying content: {ex.Message}");
                }
            }
        }

        private async void OnForkFromMessageClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;
            if (sender is FrameworkElement element && element.Tag is ChatMessageItem msg)
            {
                int index = Messages.IndexOf(msg);
                if (index < 0) return;

                try
                {
                    var branchTitle = $"{_activeSessionTitle} (Nhánh @ {index + 1})";
                    var forked = await _apiService.ForkSessionAsync(_activeSessionId, index, branchTitle);
                    if (forked != null)
                    {
                        SessionForked?.Invoke(this, forked);
                        await OpenSessionAsync(forked.Id, forked.Title);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ChatView] Error forking session: {ex.Message}");
                }
            }
        }

        private async void OnRewindToMessageClick(object sender, RoutedEventArgs e)
        {
            if (_apiService == null) return;
            if (sender is FrameworkElement element && element.Tag is ChatMessageItem msg)
            {
                int index = Messages.IndexOf(msg);
                if (index < 0) return;

                try
                {
                    var ok = await _apiService.RewindSessionAsync(_activeSessionId, index);
                    if (ok)
                    {
                        while (Messages.Count > index + 1)
                        {
                            Messages.RemoveAt(Messages.Count - 1);
                        }
                        ScrollChatToBottom();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ChatView] Error rewinding session: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            _agentMonitorCts?.Cancel();
            _agentMonitorCts?.Dispose();
            _agentMonitorCts = null;
            if (_modelSaveFailureTimer != null)
            {
                _modelSaveFailureTimer.Stop();
                _modelSaveFailureTimer.Tick -= OnModelSaveFailureTimerTick;
                _modelSaveFailureTimer = null;
            }
            if (_providerCatalogController != null)
            {
                var catalog = _providerCatalogController;
                bool owns = _ownsProviderCatalogController;
                _providerCatalogController = null;
                if (owns)
                {
                    _ = catalog.DisposeAsync().AsTask().ContinueWith(_ => { });
                }
            }
            if (_groupedCatalogs != null)
            {
                var grouped = _groupedCatalogs;
                bool ownsGrouped = _ownsGroupedCatalogs;
                grouped.StateChanged -= OnGroupedStateChanged;
                _groupedCatalogs = null;
                if (ownsGrouped)
                {
                    _ = grouped.DisposeAsync().AsTask().ContinueWith(_ => { });
                }
            }
            if (_agentController is not null)
            {
                _agentController.PropertyChanged -= OnAgentControllerChanged;
                _agentController.ActivityReceived -= OnAgentActivityReceived;
                _agentController.SummaryReceived -= OnAgentSummaryReceived;
                _agentController.AssistantDeltaReceived -= OnAgentAssistantDeltaReceived;
                _agentController.ToolOutputReceived -= OnAgentToolOutputReceived;
                _agentController.UsageReceived -= OnAgentUsageReceived;
            }
            StopAllTextSweeps();
            DetachWaveTimer();
            ClearAgentActivities();
            if (_activityFlushTimer != null)
            {
                _activityFlushTimer.Tick -= OnActivityFlushTick;
                _activityFlushTimer = null;
            }
            _typingDebounce?.Stop();
            _typingDebounce = null;
            if (_modelSaveFailureTimer != null)
            {
                _modelSaveFailureTimer.Stop();
                _modelSaveFailureTimer.Tick -= OnModelSaveFailureTimerTick;
                _modelSaveFailureTimer = null;
            }
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

        private void DetachWaveTimer()
        {
            if (_waveTimer != null)
            {
                _waveTimer.Tick -= OnWaveTimerTick;
                _waveTimer = null;
            }
        }
    }
}
