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
using RuApp.Controls;
using RuApp.Models;
using RuApp.Services;
using Windows.Storage.Streams;
using System.IO;

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

    public sealed class AssistantSegmentTemplateSelector : DataTemplateSelector
    {
        public DataTemplate? TextTemplate { get; set; }
        public DataTemplate? ActivityTemplate { get; set; }

        protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is TextSegment) return TextTemplate;
            if (item is ActivitySegment) return ActivityTemplate;
            return base.SelectTemplateCore(item, container);
        }
    }

    public sealed partial class ChatView : UserControl, IDisposable
    {
                private const double ReadingColumnMaxWidth = 820;
        private const double MessageColumnMaxWidth = 820;
        private const double NarrowWidthThreshold = 900;
        private const double MediumWidthThreshold = 1280;
        private const double NarrowHorizontalInset = 24;
        private const double MediumHorizontalInset = 48;
        private const double WideHorizontalInset = 96;
        private const double MinAvailableInputWidth = 160;
        private const int MaxImageAttachmentBytes = Services.ChatAttachmentPolicy.MaxImageAttachmentBytes;
        private const int MaxTextAttachmentBytes = Services.ChatAttachmentPolicy.MaxTextAttachmentBytes;

        private ApiService? _apiService;
        private HarnessEventSink? _harnessEvents;
        private string? _harnessScenario;
        private string _activeSessionId = "default";
        public string CurrentSessionId => _activeSessionId;
        private string _activeSessionTitle = "New Chat";
        public event EventHandler<SessionInfo>? SessionForked;
        private AgentRunController? _agentController;
        private CancellationTokenSource? _agentMonitorCts;
        private readonly AgentSessionRecovery _agentSessionRecovery = new();
        private bool _agentBusySeen;
        private AgentPermissionPreset _agentPreset = AgentPermissionPreset.Standard;
        private string? _agentPresetSessionId;
        private long? _agentPresetVersion;
        private string _agentWorkspace = Environment.CurrentDirectory;
        private string? _defaultAgentWorkspace;
        private string? _requestedWorkspace;
        private bool _agentActionPending;
        private bool _isSendingUserTurn;
        public ObservableCollection<ChatMessageItem> Messages { get; } = new();
        public ObservableCollection<ImageAttachmentItem> PendingAttachments { get; } = new();
        public ObservableCollection<AgentActivityItem> AgentActivities { get; } = new();
        private readonly Dictionary<string, AgentActivityItem> _agentActivityById = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _activitySplitOffsetById = new(StringComparer.Ordinal);
        private int _segmentFrozenLength;

        private ChatMessageItem? _activeAgentAssistantMessage;
        private string? _activeAgentRunId;
        private string? _activeWorkspaceId;
        private bool _activeAgentReplyReceived;
        private bool _activeAgentReplyCompleted;
        private AgentModelUsage? _activeAgentUsage;
        private string? _terminalFailureCardContent;
        private readonly HashSet<string> _agentDeltaKeys = new(StringComparer.Ordinal);
        private readonly AgentReplyAccumulator _replyAccumulator = new();
        private readonly AgentReplyRenderScheduler _replyRenderScheduler;
        private readonly object _accumulatorLock = new();

        public ChatView()
        {
            this.InitializeComponent();
            // A size chosen while the transcript is open repaints the rows already on
            // screen; without this only rows created afterwards would pick it up.
            TranscriptTypography.Instance.PropertyChanged += OnTranscriptTypographyChanged;
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
            this.Loaded += (s, e) =>
            {
                ApplyInputWidth();
                EnsureNewMessagesPillWired();
            };
            InitializeChatHoverEffects();
            UpdateViewMode();
        }

        private void InitializeChatHoverEffects()
        {
            var canvas = (Brush)Application.Current.Resources["RuCanvasBrush"];
            var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            var ink = (Brush)Application.Current.Resources["RuInkBrush"];
            var muted = (Brush)Application.Current.Resources["RuMutedBrush"];

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
            if (ChatContentGrid != null)
            {
                ChatContentGrid.Visibility = hasMessages ? Visibility.Visible : Visibility.Collapsed;
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

        /// <summary>
        /// The last settings payload read from the backend, used to name the provider
        /// that is missing a key without a second request while reporting a failure.
        /// </summary>
        private AppSettingsDto? _currentSettings;
        private string _selectedReasoningEffort = "default";
        private DispatcherTimer? _modelSaveFailureTimer;

        public void Initialize(
            ApiService apiService,
            HarnessEventSink? harnessEvents = null,
            string? harnessScenario = null,
            AgentRunController? agentController = null,
            string? defaultAgentWorkspace = null,
            ProviderCatalogController? providerCatalogController = null,
            GroupedProviderCatalogController? groupedProviderCatalogs = null)
        {
            _apiService = apiService;
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
            ApprovalPanel.DecisionRequested += OnApprovalDecisionRequested;
            UpdateAgentComposerVisuals();
            UpdateAgentInlineState();

            ChatInputTextBox.TextChanged += OnChatInputTyping;

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
                // Kept so a failed send can name the provider that has no key
                // without asking the backend again while reporting a failure.
                _currentSettings = settings;
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
            // This walks the whole transcript, so running it on every streamed
            // snapshot turns chat history length into per-frame UI cost and
            // starves the activity feed. A one-second-old meter is plenty fresh.
            long now = Environment.TickCount64;
            if (now - _lastContextUsageTick < ContextUsageMinIntervalMs)
            {
                return;
            }

            _lastContextUsageTick = now;
            string currentModel = !string.IsNullOrWhiteSpace(_groupedCatalogs?.State.SelectedModel)
                ? _groupedCatalogs.State.SelectedModel
                : _selectedModel;
            int? catalogCtx = GetCurrentModelContextLength();
            var (maxTokens, displayLimit) = ModelContextSpecs.GetContextSpec(currentModel, catalogCtx);

            // Include draft input so the meter moves while typing, not only after send.
            string draftText = string.Empty;
            try
            {
                draftText = ChatInputTextBox?.Text ?? string.Empty;
            }
            catch
            {
                // Input box may not be ready during init; estimate stays history-only.
            }

            var (totalChars, totalImages) = AgentContextMeterPolicy.Measure(
                Messages.Select(message => (message.Content, message.Attachments?.Count ?? 0)),
                draftText,
                PendingAttachments.Count);

            var reportedTotals = Messages
                .Where(message => message.Usage is not null)
                .Select(message => ResolveUsageTotal(message.Usage!))
                .ToList();
            if (_activeAgentUsage is { } activeUsage)
            {
                reportedTotals.Add(ResolveUsageTotal(activeUsage));
            }

            int reportedTokens = AgentContextMeterPolicy.MaxReportedTokens(reportedTotals);

            int estimatedTokens = ModelContextSpecs.EstimateTokens(totalChars, totalImages);
            var meter = AgentContextMeterPolicy.Evaluate(maxTokens, displayLimit, estimatedTokens, reportedTokens);
            int usedTokens = meter.UsedTokens;

            if (meter.HasBudget)
            {
                double percentage = meter.Percentage;

                ContextProgressRing.Value = percentage;
                ContextProgressBar.Value = percentage;
                ContextProgressBar.Visibility = Visibility.Visible;

                var brush = meter.Severity switch
                {
                    AgentContextMeterSeverity.Danger =>
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuDangerBrush"],
                    AgentContextMeterSeverity.Warning =>
                        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuWarningBrush"],
                    _ => (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuAccentBrush"],
                };

                ContextProgressRing.Foreground = brush;
                ContextProgressBar.Foreground = brush;

                ContextUsedLabel.Text = $"{ModelContextSpecs.FormatTokens(usedTokens)} / {meter.DisplayLimit}";
                ContextPercentLabel.Text = $"{percentage:0.#}%";
                ContextPercentLabel.Foreground = meter.Severity == AgentContextMeterSeverity.Normal
                    ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RuMutedBrush"]
                    : brush;
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
            _composerDrafts.Save(_activeSessionId, ChatInputTextBox.Text);
            ScheduleContextMeterUpdate();
            if (string.IsNullOrEmpty(ChatInputTextBox.Text))
            {
                return;
            }
        }

        private DispatcherQueueTimer? _contextMeterTimer;
        private bool _contextMeterScheduled;
        private long _lastContextUsageTick;
        private const long ContextUsageMinIntervalMs = 1000;

        private void ScheduleContextMeterUpdate()
        {
            if (_suppressContextUsageUpdate)
            {
                return;
            }

            if (_contextMeterTimer is null)
            {
                _contextMeterTimer = DispatcherQueue.CreateTimer();
                _contextMeterTimer.Interval = Services.UiTiming.ContextMeterDebounce;
                _contextMeterTimer.Tick += (_, _) =>
                {
                    _contextMeterTimer?.Stop();
                    _contextMeterScheduled = false;
                    UpdateContextUsage();
                };
            }

            if (_contextMeterScheduled)
            {
                return;
            }

            _contextMeterScheduled = true;
            _contextMeterTimer.Start();
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

            if (!DispatcherQueue.TryEnqueue(() => EnqueueAgentActivity(activity)))
            {
                System.Diagnostics.Debug.WriteLine("[ChatView] Dropped activity frame: dispatcher queue rejected enqueue.");
            }
        }

        private void EnqueueAgentActivity(AgentActivityModel activity)
        {
            // An activity frame may arrive while the turn is armed but the
            // controller has not flipped busy yet (the Thinking Started frame
            // is published before the first model call completes): arm the
            // assistant anchor now so the first frames are never dropped.
            EnsureAgentActivityAnchor();
            _pendingAgentActivities.Add(activity);
            if (_pendingAgentActivities.Count == 1 && _activityFlushTimer?.IsRunning != true)
            {
                // Lone frame: render on the next dispatcher turn instead of
                // waiting out the batch window. Burst traffic still falls
                // through to the timer below and renders as one batch.
                if (DispatcherQueue.TryEnqueue(FlushAgentActivities))
                {
                    return;
                }
            }

            if (_activityFlushTimer is null)
            {
                _activityFlushTimer = DispatcherQueue.CreateTimer();
                _activityFlushTimer.Interval = Services.UiTiming.ActivityFlushBatch;
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

        /// <summary>
        /// The activity flush timer can lose the race with the run's terminal
        /// transition, and frames are ignored once the controller is idle: the
        /// last frame of a run would be dropped and a running row (for example
        /// "Thinking") would keep its progress affordance forever. Drain the
        /// queue without the idle gate, then settle anything still running.
        /// </summary>
        private void SettleAgentActivitiesAfterRun()
        {
            if (_pendingAgentActivities.Count > 0)
            {
                var queued = _pendingAgentActivities.ToArray();
                _pendingAgentActivities.Clear();
                foreach (var activity in queued)
                {
                    ApplyAgentActivity(activity, allowWhenIdle: true);
                }
            }

            AgentActivityGrouper.SettleRunningRows(AgentActivities);
            AgentActivityGrouper.Reconcile(AgentActivities);
        }

        private void ApplyAgentActivity(AgentActivityModel activity, bool allowWhenIdle = false)
        {
            if ((!allowWhenIdle && !HasRunInHand())
                || string.IsNullOrWhiteSpace(activity.ActivityId))
            {
                return;
            }

            if (_agentActivityById.TryGetValue(activity.ActivityId, out var existing))
            {
                // Every frame is applied as it arrives. A completion used to be held
                // for a minimum window to hide the flicker of a short row, which read
                // as the feed standing still and then jumping: the row kept its
                // running affordance while the tool had already finished.
                if (existing.UpdateFrom(activity))
                {
                    TrackTerminalToolState(existing);
                }
            }
            else
            {
                var item = new AgentActivityItem(activity);

                _agentActivityById.Add(item.ActivityId, item);
                InsertActivityInFeedOrder(item);
                FreezeTextSegmentForActivity(item);
                TrackTerminalToolState(item);
            }
        }

        /// <summary>
        /// Inserts a new activity row so tool progress reads chronologically:
        /// a settled Thinking row stays above the tool rows that followed it
        /// instead of sinking below them, matching the Codex/OpenCode feel.
        /// </summary>
        private void InsertActivityInFeedOrder(AgentActivityItem item)
        {
            int insertAt = AgentActivities.Count;
            if (item.Kind == RuAgent.Protocol.Harness.AgentActivityKind.Thinking)
            {
                while (insertAt > 0
                    && AgentActivities[insertAt - 1].Kind != RuAgent.Protocol.Harness.AgentActivityKind.Thinking)
                {
                    insertAt--;
                }
            }

            AgentActivities.Insert(insertAt, item);
        }

        /// <summary>
        /// Arms the live assistant bubble for activity-only progress while the
        /// turn is still being sent: SendCurrentMessageAsync holds
        /// _isSendingUserTurn across the whole create-run await, so the first
        /// SSE frames (Thinking Started) land before the controller flips busy
        /// or the first text delta arrives. A no-op once the anchor exists.
        /// </summary>
        private void EnsureAgentActivityAnchor()
        {
            if (_activeAgentAssistantMessage is not null || !_isSendingUserTurn)
            {
                return;
            }

            _activeAgentAssistantMessage = new ChatMessageItem
            {
                Role = "assistant",
                Content = string.Empty,
                IsStreaming = true,
                SessionId = _activeSessionId,
            };
            _activeAgentAssistantMessage.AgentActivities = AgentActivities;
            _segmentFrozenLength = 0;
            Messages.Add(_activeAgentAssistantMessage);
        }

        /// <summary>
        /// Freezes the prose that arrived before a new activity into a settled
        /// text segment, so the feed stays chronological: narration above, the
        /// tool row below it, fresh prose in a new live tail. Only touches the
        /// active message while it still shares the live activity collection.
        /// </summary>
        private void FreezeTextSegmentForActivity(AgentActivityItem item)
        {
            var message = _activeAgentAssistantMessage;
            if (message is null || !ReferenceEquals(message.AgentActivities, AgentActivities))
            {
                return;
            }

            var content = message.Content ?? string.Empty;
            var frozen = Math.Min(_segmentFrozenLength, content.Length);
            var segments = message.Segments;
            if (segments is null)
            {
                segments = new ObservableCollection<AssistantSegment>();
                message.Segments = segments;
            }

            if (segments.Count > 0 && segments[^1] is TextSegment tail)
            {
                tail.Text = content.Substring(frozen);
                tail.IsError = message.IsError;
            }
            else if (content.Length > frozen)
            {
                segments.Add(new TextSegment(content.Substring(frozen)) { IsError = message.IsError });
            }

            _activitySplitOffsetById[item.ActivityId] = content.Length;
            _segmentFrozenLength = content.Length;
            segments.Add(new ActivitySegment(item.ActivityId, item));
            segments.Add(new TextSegment(string.Empty) { IsError = message.IsError });
        }

        private void UpdateLiveTailText(ChatMessageItem? message)
        {
            if (message?.Segments is { Count: > 0 } segments
                && segments[^1] is TextSegment tail)
            {
                var content = message.Content ?? string.Empty;
                var frozen = Math.Min(_segmentFrozenLength, content.Length);
                tail.Text = content.Substring(frozen);
            }
        }

        private List<ActivitySplitOffset> TakeSplitOffsetSnapshot(IEnumerable<AgentActivityItem> items)
        {
            var snapshot = new List<ActivitySplitOffset>();
            foreach (var item in items)
            {
                if (item != null && _activitySplitOffsetById.TryGetValue(item.ActivityId, out var offset))
                {
                    snapshot.Add(new ActivitySplitOffset(item.ActivityId, offset));
                }
            }

            return snapshot;
        }

        private static void SyncSegmentErrorState(ChatMessageItem message)
        {
            if (message.Segments is null)
            {
                return;
            }

            foreach (var segment in message.Segments)
            {
                if (segment is TextSegment text)
                {
                    text.IsError = message.IsError;
                }
            }
        }

        private static void RebuildMessageSegments(ChatMessageItem message)
        {
            if (message.AgentActivities is { Count: > 0 } activities
                && message.ActivitySplitOffsets != null
                && !string.IsNullOrEmpty(message.Content))
            {
                message.Segments = new ObservableCollection<AssistantSegment>(
                    ActivitySegmenter.BuildSettled(message.Content, activities, message.ActivitySplitOffsets));
                SyncSegmentErrorState(message);
            }
            else
            {
                message.Segments = null;
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
                TerminalPanelControl.NoteToolLabel(item.DisplayText);
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
            if (!AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()).IsSessionBusy)
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

            _terminalFailureCardContent = content.ToString();
            Messages.Add(new ChatMessageItem
            {
                Role = "assistant",
                Content = _terminalFailureCardContent,
                IsError = true,
            });
            ScrollChatToBottom();
        }

        private void OnAgentAssistantDeltaReceived(object? sender, AgentAssistantDeltaModel delta)
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => OnAgentAssistantDeltaReceived(sender, delta));
                return;
            }

            AgentRunModel? run = _agentController?.CurrentRun;
            if (run is null || !HasRunInHand())
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
                EnsureAgentActivityAnchor();
                lock (_accumulatorLock)
                {
                    activeAssistantMessage = _activeAgentAssistantMessage;
                }
                if (activeAssistantMessage is null)
                {
                    return;
                }
            }

            activeRunId ??= run.RunId;
            lock (_accumulatorLock)
            {
                _activeAgentRunId = activeRunId;
                _replyAccumulator.Rebind(_activeSessionId, activeRunId);
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
            bool accepted = delta.IsReset
                ? _replyAccumulator.TryReset(run.RunId, delta.ModelStepId, delta.TransientSequence)
                : _replyAccumulator.TryAppend(run.RunId, delta.ModelStepId, delta.TransientSequence, delta.Text);
            if (!accepted)
            {
                // If no active accumulator (e.g., after Invalidate), begin with real runId and retry once
                if (!_replyAccumulator.HasSession
                    || !string.Equals(_replyAccumulator.SessionId, _activeSessionId, StringComparison.Ordinal))
                {
                    return;
                }

                _replyAccumulator.Rebind(_activeSessionId, run.RunId);
                lock (_accumulatorLock)
                {
                    _activeAgentRunId = run.RunId;
                }
                accepted = delta.IsReset
                    ? _replyAccumulator.TryReset(run.RunId, delta.ModelStepId, delta.TransientSequence)
                    : _replyAccumulator.TryAppend(run.RunId, delta.ModelStepId, delta.TransientSequence, delta.Text);
                if (!accepted)
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

            // Non-text deltas still schedule a render: skip the layout + scroll
            // pass when the text did not actually grow.
            if (!string.Equals(_activeAgentAssistantMessage.Content, snapshot.Text, StringComparison.Ordinal))
            {
                _activeAgentAssistantMessage.Content = snapshot.Text;
                UpdateLiveTailText(_activeAgentAssistantMessage);
                ScrollChatToBottom();
            }

            _activeAgentAssistantMessage.IsStreaming = true;
            UpdateContextUsage();
        }

        private void ClearAgentActivities()
        {
            StopAllTextSweeps();
            _activityFlushTimer?.Stop();
            _pendingAgentActivities.Clear();
            _agentActivityById.Clear();
            _activitySplitOffsetById.Clear();
            _segmentFrozenLength = 0;
            AgentActivities.Clear();
            _agentBusySeen = false;
        }

        private void DetachAgentActivitiesFromMessages()
        {
            foreach (var message in Messages)
            {
                if (ReferenceEquals(message.AgentActivities, AgentActivities))
                {
                    message.AgentActivities = null;
                    message.Segments = null;
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
                    message.ActivitySplitOffsets = message.AgentActivities != null
                        ? TakeSplitOffsetSnapshot(message.AgentActivities)
                        : null;
                }
            }
            ClearAgentActivities();
            ClearTerminalBlocks();
        }

        // ── Terminal output panel ──────────────────────────────
        // The block list, its bound, the label carried onto the next block, and the
        // show/hide state all live in TerminalPanelControl. The view only forwards.
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
            if (!AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()).IsSessionBusy)
            {
                return;
            }

            TerminalPanelControl.AppendOutput(output.ExecutionId, output.Text);
        }

        private void CompleteTerminalBlock(string label, bool failed) =>
            TerminalPanelControl.CompleteBlock(label, failed);

        private void ClearTerminalBlocks() => TerminalPanelControl.Clear();

        // ── Activity text wave animation ──────────────────────────────
        private readonly Dictionary<TextBlock, AgentActivityItem> _activeWaveTextBlocks = new();
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
            RenderTodoChecklist(grid, item);

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

        /// <summary>
        /// Draws the agent's task list on the activity that carries one.
        /// </summary>
        /// <remarks>
        /// The rows are pushed into the control rather than bound, because the payload is
        /// an array that arrives whole on each activity: there is nothing to observe and
        /// no per-row change to propagate.
        ///
        /// A row without a list is left alone rather than cleared. Only the activity that
        /// carries a list owns the checklist, so an unrelated activity arriving later
        /// cannot blank the plan.
        /// </remarks>
        private static void RenderTodoChecklist(DependencyObject root, AgentActivityItem item)
        {
            if (item.Todos is not { Count: > 0 } todos)
            {
                return;
            }

            foreach (var descendant in EnumerateDescendants(root))
            {
                if (descendant is Controls.TodoChecklistControl checklist)
                {
                    checklist.Render(todos);
                    return;
                }
            }
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
                StartTextSweep(textBlock, item);
            }
            else
            {
                StopTextSweep(textBlock);
                var targetBrush = item.IsFailed
                    ? (SolidColorBrush)Application.Current.Resources["RuDangerBrush"]
                    : (SolidColorBrush)Application.Current.Resources["RuInkBrush"];

                textBlock.Foreground = targetBrush;
                // Waiting rows need user action: italic sets them apart without
                // spending a color that would fail AA on the light theme.
                textBlock.FontStyle = item.IsWaiting
                    ? Windows.UI.Text.FontStyle.Italic
                    : Windows.UI.Text.FontStyle.Normal;
            }
        }

        private void StartTextSweep(TextBlock textBlock, AgentActivityItem item)
        {
            _activeWaveTextBlocks[textBlock] = item;

            if (_waveTimer == null)
            {
                _waveTimer = DispatcherQueue.CreateTimer();
                _waveTimer.Interval = Services.UiTiming.WaveFrame; // 25 FPS
                _waveTimer.Tick += OnWaveTimerTick;
            }

            if (!_waveTimer.IsRunning)
            {
                _waveStopwatch.Restart();
                _waveTimer.Start();
            }

            UpdateWaveTextBlock(textBlock, 0.0, ResolveWaveBaseColor());
        }

        private static Windows.UI.Color ResolveWaveBaseColor()
        {
            var mutedBrush = Application.Current.Resources["RuMutedBrush"] as SolidColorBrush;
            return mutedBrush?.Color ?? Windows.UI.Color.FromArgb(255, 148, 163, 184);
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
            double phase = (elapsed / Services.UiTiming.ActivityWavePeriodSeconds) % 1.0;

            // One resource lookup per tick, not per block: theme stays live
            // because the color is re-resolved on every tick.
            var baseColor = ResolveWaveBaseColor();

            List<TextBlock>? deadBlocks = null;
            foreach (var kvp in _activeWaveTextBlocks)
            {
                var tb = kvp.Key;
                var owner = kvp.Value;
                if (tb.XamlRoot == null
                    || !owner.IsRunning
                    || !owner.IsVisibleInFeed)
                {
                    deadBlocks ??= new List<TextBlock>();
                    deadBlocks.Add(tb);
                    continue;
                }
                UpdateWaveTextBlock(tb, phase, baseColor);
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

        private static void UpdateWaveTextBlock(TextBlock tb, double phase, Windows.UI.Color baseColor)
        {
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
            AgentStatusText.Text = status;
            AgentStatusText.Visibility = string.IsNullOrWhiteSpace(status)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        // The markup binds its initial send face to these, so the XAML and the runtime
        // assignment below read the same vocabulary instead of holding two copies.
        public string SendFaceGlyph => AgentComposerPresentation.SendGlyph;

        public string SendFaceName => AgentComposerPresentation.SendName;

        private void UpdateAgentComposerVisuals()
        {
            if (AgentModeLabel is null)
            {
                return;
            }

            AgentModeLabel.Text = AgentComposerPresentation.AgentModeLabelText;
            AgentModeIcon.Glyph = AgentComposerPresentation.AgentModeGlyph;
            AgentModeButton.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(
                AgentModeButton,
                AgentComposerPresentation.AgentModeName);
            if (AgentPresetBox is not null && AgentPresetBox.SelectedIndex < 0 && AgentPresetBox.Items.Count > 0)
            {
                AgentPresetBox.SelectedIndex = 0;
            }
        }

        public AgentPermissionPreset CurrentAgentPreset => _agentPreset;

        public void SetAgentPreset(AgentPermissionPreset preset)
        {
            _agentPreset = preset;
            if (AgentPresetBox is not null)
            {
                AgentPresetBox.SelectedIndex = preset == AgentPermissionPreset.Trusted ? 1 : 0;
            }
            UpdateAgentInlineState();
        }

        private void OnAgentPresetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSendingUserTurn)
            {
                return;
            }

            _agentPreset = SelectedAgentPreset();
            UpdateAgentInlineState();
        }

        private AgentPermissionPreset SelectedAgentPreset()
        {
            if (AgentPresetBox?.SelectedItem is ComboBoxItem item
                && string.Equals(item.Tag as string, "trusted", StringComparison.Ordinal))
            {
                return AgentPermissionPreset.Trusted;
            }

            return AgentPermissionPreset.Standard;
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
                    SendButtonIcon.Glyph = AgentComposerPresentation.StopGlyph;
                    SendButtonIcon.Margin = new Thickness(0);
                    if (Application.Current.Resources.TryGetValue("RuOnAccentBrush", out var onAccentStop) && onAccentStop is Brush onAccentStopBrush)
                    {
                        SendButtonIcon.Foreground = onAccentStopBrush;
                    }
                }
                AutomationProperties.SetName(SendButton, AgentComposerPresentation.StopName);
            }
            else
            {
                if (SendButtonIcon is not null)
                {
                    SendButtonIcon.Glyph = AgentComposerPresentation.SendGlyph;
                    SendButtonIcon.Margin = new Thickness(-1, 1, 0, 0);
                    if (Application.Current.Resources.TryGetValue("RuOnAccentBrush", out var onAccentBrush) && onAccentBrush is Brush onAccent)
                    {
                        SendButtonIcon.Foreground = onAccent;
                    }
                }
                AutomationProperties.SetName(SendButton, AgentComposerPresentation.SendName);
            }
        }

        private void UpdateAgentInlineState()
        {
            bool agentBusy = AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()).IsSessionBusy;
            if (_agentBusySeen && !agentBusy)
            {
                SettleAgentActivitiesAfterRun();
            }

            _agentBusySeen = agentBusy;

            // The rules live in AgentInlinePanelPolicy; this method only projects the
            // controller and assigns the result to controls.
            var controller = _agentController;
            var hasController = controller is not null;
            bool runBelongsToAnotherSession = controller?.CurrentRun is { } currentRun
                && !string.Equals(currentRun.SessionId, _activeSessionId, StringComparison.Ordinal);
            bool keepTerminalFailureVisible = hasController
                && AgentFailurePresentation.ShouldKeepVisible(
                    controller!.CurrentRun?.Status,
                    ResolveFailureReason(controller));
            bool isRunningForStatus = AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()).ShowsStopAffordance;
            string statusText = hasController
                ? AgentFailurePresentation.InlineFailureStatus(
                    controller!.CurrentRun?.Status,
                    ResolveFailureReason(controller),
                    isRunningForStatus,
                    HasRenderedAgentReply())
                : string.Empty;

            var state = AgentInlinePanelPolicy.Evaluate(new AgentInlinePanelInput(
                HasController: hasController,
                RunBelongsToAnotherSession: runBelongsToAnotherSession,
                IsBusy: controller?.IsBusy == true,
                CanCancel: controller?.CanCancel == true,
                HasPendingApproval: controller?.HasPendingApproval == true,
                NeedsWorkspaceTrust: controller?.NeedsWorkspaceTrust == true,
                CanContinue: controller?.CanContinue == true,
                KeepTerminalFailureVisible: keepTerminalFailureVisible,
                RunPending: IsActiveAgentRunPending(),
                StatusText: statusText,
                InteractionPending: _agentActionPending));

            UpdateSendButtonMode(isAgentRunning: state.IsRunning);

            if (state.ClearActivityVisibility)
            {
            }

            if (state.AutoApproveWorkspaceTrust && controller is not null)
            {
                // Workspace trust is auto-granted: a project-root chat starts silently.
                _ = controller.TrustAsync(approve: true);
            }

            AgentInlinePanel.Visibility = state.ShowInlinePanel ? Visibility.Visible : Visibility.Collapsed;

            AgentStatusText.Text = state.ShowInlinePanel ? state.StatusText : string.Empty;
            AgentStatusText.Visibility = string.IsNullOrWhiteSpace(AgentStatusText.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;
            AgentStopButton.Visibility = state.ShowStop ? Visibility.Visible : Visibility.Collapsed;

            if (state.ShowApproval && controller is not null)
            {
                string summary = AgentApprovalSummaryPresentation.Render(
                    controller.PendingApprovalSummary);
                string? terminalCommand = AgentApprovalPresentation.ResolveTerminalCommand(
                    controller.PendingApprovalCommand,
                    AgentActivities);

                ApprovalPanel.Show(summary, terminalCommand);

                // Approval arrives via SSE while the user may be scrolled up; bring
                // the Approve/Deny prompt into view and put the keyboard on it.
                ScrollChatToBottom();
                ApprovalPanel.FocusDecision();
            }
            else
            {
                ApprovalPanel.Hide();
            }

            ApprovalPanel.SetDecisionEnabled(!state.SuppressInteraction);
            if (AgentStopButton is not null)
            {
                AgentStopButton.IsEnabled = !state.SuppressInteraction;
            }
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
        private static string RenderApprovalSummary(string? summary) =>
            AgentApprovalSummaryPresentation.Render(summary);

        private static string BoundedDisplay(string value, int maxLength) =>
            AgentApprovalSummaryPresentation.Bounded(value, maxLength);

        // ── Run activity ───────────────────────────────────────
        // "Is a run in hand for this session?" is one question with one answer, asked
        // by the send guard, the delta gate, the tool-output gate, the inline panel,
        // and the composer mode. It lives in AgentRunActivityPolicy; the view only
        // projects its own state and the controller's into it.
        private AgentRunActivityInput SnapshotRunActivity()
        {
            string? runSessionId = _agentController?.CurrentRun?.SessionId;
            return new AgentRunActivityInput(
                IsSendingTurn: _isSendingUserTurn,
                HasRun: _agentController?.CurrentRun is not null,
                RunSessionId: runSessionId,
                ActiveSessionId: _activeSessionId,
                CanCancel: _agentController?.CanCancel == true,
                HasPendingApproval: _agentController?.HasPendingApproval == true,
                NeedsWorkspaceTrust: _agentController?.NeedsWorkspaceTrust == true,
                IsBusy: _agentController?.IsBusy == true);
        }

        /// <summary>
        /// True while this session has a run in hand, including the send window. The
        /// synchronous send guard lives here: entry points (Enter key, Send click) use
        /// this, so a second Enter during the create-run await cannot produce a
        /// duplicate user bubble and a duplicate run.
        /// </summary>
        private bool IsActiveAgentRunPending() =>
            AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()).IsGuarded;

        /// <summary>
        /// Whether a live stream frame belongs in this chat: the controller is busy on
        /// this session, or the view is between tapping Send and the run existing. Every
        /// live-stream gate asks this one question.
        /// </summary>
        private bool HasRunInHand() =>
            AgentRunActivityPolicy.Evaluate(SnapshotRunActivity()) is { IsSessionBusy: true }
                or { Reason: AgentRunActivityReason.Sending };

        /// <summary>
        /// The controller's own view of the run, without the send guard.
        /// SendAgentTurnAsync must use this: it already holds the guard, so asking the
        /// guarded question there would block its own caller.
        /// </summary>
        private bool IsControllerRunPending() =>
            AgentRunActivityPolicy.IsControllerRunPending(SnapshotRunActivity());

        private void ResetAgentState()
        {
            ClearAgentActivities();

            var monitor = _agentMonitorCts;
            _agentMonitorCts = null;
            monitor?.Cancel();
            monitor?.Dispose();
            _agentActionPending = false;
            _isSendingUserTurn = false;
            _activeAgentUsage = null;
            _agentPreset = AgentPermissionPreset.Standard;
            _agentPresetSessionId = null;
            _agentPresetVersion = null;
            AbandonActiveAgentReply();

            UpdateAgentComposerVisuals();
            if (AgentPresetBox is not null && AgentPresetBox.SelectedIndex != 0)
            {
                AgentPresetBox.SelectedIndex = 0;
            }
            AgentInlinePanel.Visibility = Visibility.Collapsed;
            ApprovalPanel.Hide();
            AgentStopButton.Visibility = Visibility.Collapsed;
            AgentStatusText.Text = string.Empty;
            AgentStatusText.Visibility = Visibility.Collapsed;
            UpdateSendButtonMode(isAgentRunning: false);
        }


        private static string StripDisplayMarkers(string content) => ChatDisplayMarkers.Strip(content);

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
                if (_isSendButtonInStopMode || IsActiveAgentRunPending())
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

        private void OnInputDragOver(object sender, DragEventArgs e)
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "Drop to attach files / images";
                e.DragUIOverride.IsContentVisible = true;
            }
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
                if (imageCount > ChatAttachmentPolicy.MaxImageCount)
                {
                    ShowAgentInlineStatus(ChatAttachmentPolicy.TooManyImagesStatus);
                    return;
                }

                ChatInputTextBox.Text = string.Empty;
                PendingAttachments.Clear();
                AttachmentPreviewContainer.Visibility = Visibility.Collapsed;
                ClearComposerDraft(_activeSessionId);


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

                await SendAgentTurnAsync(effectivePrompt, CancellationToken.None, attachments: attachments, displayUserText: text);
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

        private async Task SendAgentTurnAsync(
            string text,
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
                return;
            }


            await EnsureAgentSessionAsync(cancellationToken);
            string workspace = AgentWorkspaceSelection.Resolve(_requestedWorkspace, _defaultAgentWorkspace)
                ?? _defaultAgentWorkspace
                ?? throw new InvalidOperationException("App root workspace is not configured.");

            _agentWorkspace = workspace;

            try
            {
                AgentPermissionPreset preset = SelectedAgentPreset();
                if (preset == AgentPermissionPreset.Trusted && !await ConfirmTrustedAsync())
                {
                    if (AgentPresetBox is not null)
                    {
                        AgentPresetBox.SelectedIndex = 0;
                    }
                    _agentPreset = AgentPermissionPreset.Standard;
                    UpdateAgentInlineState();
                    return;
                }

                _agentPreset = preset;
                long presetVersion = await EnsureAgentPresetAsync(preset, cancellationToken);
                // The trusted choice is stored server-side per session: the run
                // request carries the same wire value so the stored selection and
                // the run agree, but the stored selection stays authoritative.
                string wirePreset = preset == AgentPermissionPreset.Trusted ? "trusted" : "standard";
                var imagePayloads = attachments?
                    .Where(a => a.IsImage && (!a.IsImage || !string.IsNullOrEmpty(a.Base64Data) || !string.IsNullOrEmpty(a.FilePath)))
                    .Select(a => !string.IsNullOrEmpty(a.Base64Data) ? $"data:{a.MimeType};base64,{a.Base64Data}" : a.FilePath!)
                    .ToList();

                var request = new CreateAgentRunRequest(
                    _activeSessionId,
                    workspace,
                    text,
                    wirePreset,
                    presetVersion,
                    Guid.NewGuid().ToString("N"),
                    imagePayloads);

                _activeWorkspaceId = null;

                StartNewAgentActivityRun();
                BeginAgentReply(text, attachments, displayUserText, addUserMessage);
                JumpToLatestForNewTurn();
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
                // Preserve the completion gate while replacing the stale
                // session and retrying this same logical turn once.
                AbandonActiveAgentReply(removeUserMessage: true);
                await RecoverAgentSessionAsync(cancellationToken);
                await SendAgentTurnAsync(
                    text,
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

        private void BeginAgentReply(string text, List<ImageAttachmentItem>? attachments = null, string? displayUserText = null, bool addUserMessage = true)
        {
            _activeAgentRunId = null;
            _activeAgentReplyReceived = false;
            _activeAgentReplyCompleted = false;
            _activeAgentUsage = null;
            _terminalFailureCardContent = null;
            _agentDeltaKeys.Clear();
            _replyRenderScheduler.Invalidate();
            lock (_accumulatorLock)
            {
                _replyAccumulator.Begin(_activeSessionId, _activeSessionId);
            }

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
            _segmentFrozenLength = 0;
            Messages.Add(_activeAgentAssistantMessage);
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
                UpdateLiveTailText(_activeAgentAssistantMessage);
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

            // The failure card is UI-only copy and the reload above rebuilds the
            // transcript from session history, so a terminal failure without a
            // rendered reply would otherwise leave no trace. Re-append the
            // actionable card (or the copy cached from the live summary).
            if (TerminalFailureCardPolicy.ShouldAppend(
                    _agentController?.CurrentRun?.Status,
                    HasRenderedAgentReply(),
                    Messages.Any(message => message.Role == "assistant" && message.IsError)))
            {
                Messages.Add(new ChatMessageItem
                {
                    Role = "assistant",
                    Content = _terminalFailureCardContent
                        ?? TerminalFailureCardPolicy.Compose(
                            _agentController is null ? null : ResolveFailureReason(_agentController),
                            _agentController?.CanContinue == true),
                    IsError = true,
                });
                ScrollChatToBottom();
            }

            _terminalFailureCardContent = null;
            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _agentDeltaKeys.Clear();
            UpdateAgentInlineState();
        }
        public static bool IsContinueResumeCommand(string text, IReadOnlyCollection<ImageAttachmentItem> attachments)
        {
            ArgumentNullException.ThrowIfNull(text);
            ArgumentNullException.ThrowIfNull(attachments);
            return attachments.Count == 0
                && string.Equals(text.Trim(), "Continue", StringComparison.OrdinalIgnoreCase);
        }

        private static Task<byte[]?> ReadBoundedBytesAsync(System.IO.Stream stream, int maximumBytes) =>
            Services.ChatAttachmentPolicy.ReadBoundedBytesAsync(stream, maximumBytes);

        private static bool IsImageFile(string name) => Services.ChatAttachmentPolicy.IsImageFile(name);

        private static bool IsTextOrCodeFile(string name) => Services.ChatAttachmentPolicy.IsTextOrCodeFile(name);

        private void FailActiveAgentReply(Exception exception)
        {
            AgentReplySnapshot? pending = _replyRenderScheduler.Flush();
            if (pending is not null && _activeAgentAssistantMessage is not null)
            {
                _activeAgentAssistantMessage.Content = pending.Text;
                UpdateLiveTailText(_activeAgentAssistantMessage);
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
                    UpdateLiveTailText(_activeAgentAssistantMessage);
                }
                _activeAgentAssistantMessage.IsStreaming = false;
                _activeAgentAssistantMessage.IsError = !hasReply;
                SyncSegmentErrorState(_activeAgentAssistantMessage);
            }

            _activeAgentAssistantMessage = null;
            _activeAgentRunId = null;
            _agentDeltaKeys.Clear();
        }

        private void AbandonActiveAgentReply(bool removeUserMessage = false)
        {
            _replyRenderScheduler.Invalidate();

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
            _agentPresetSessionId = null;
            _agentPresetVersion = null;
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

        private async Task<long> EnsureAgentPresetAsync(
            AgentPermissionPreset preset,
            CancellationToken cancellationToken)
        {
            if (_agentController is null)
            {
                throw new InvalidOperationException("Agent controller is unavailable.");
            }

            if (preset == AgentPermissionPreset.Standard
                && string.Equals(_agentPresetSessionId, _activeSessionId, StringComparison.Ordinal)
                && _agentPreset == AgentPermissionPreset.Standard
                && _agentPresetVersion is long knownStandardVersion)
            {
                return knownStandardVersion;
            }

            if (string.Equals(_agentPresetSessionId, _activeSessionId, StringComparison.Ordinal)
                && _agentPreset == preset
                && _agentPresetVersion is long knownVersion)
            {
                return knownVersion;
            }

            string wirePreset = preset == AgentPermissionPreset.Trusted ? "trusted" : "standard";
            AgentApiException? lastConflict = null;
            for (long expectedVersion = 0; expectedVersion <= 3; expectedVersion++)
            {
                try
                {
                    long version = await _agentController.SetSessionPresetAsync(
                        _activeSessionId,
                        wirePreset,
                        expectedVersion,
                        interactive: true,
                        cancellationToken);
                    _agentPresetSessionId = _activeSessionId;
                    _agentPresetVersion = version;
                    return version;
                }
                catch (AgentApiException ex) when (ex.ErrorCode == "preset_version_conflict")
                {
                    lastConflict = ex;
                }
            }

            if (lastConflict is not null)
            {
                throw lastConflict;
            }

            throw new InvalidOperationException("Could not update the Agent permission preset.");
        }

        private async Task<bool> ConfirmTrustedAsync()
        {
            if (XamlRoot is null)
            {
                return false;
            }

            var dialog = new ContentDialog
            {
                Title = "Trust this session?",
                Content = "Trusted runs with fewer approval prompts on a clean workspace. Risky actions outside the workspace still require approval, and workspace trust is still required.",
                PrimaryButtonText = "Trust",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
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
                AgentApiException api => AgentFailurePresentation.RenderStartFailure(api.ErrorCode, api.Message),
                _ => $"Coding Agent could not start: {exception.Message}"
            };

            // A send that failed because no provider is configured is the first
            // failure a new user meets. Saying which provider is missing a key, and
            // offering the screen that fixes it, turns a dead end into one click.
            if (ProviderSetupGuidance.IsMissingKey(_currentSettings, _selectedBackend))
            {
                status = ProviderSetupGuidance.MissingKeyMessage(_currentSettings, _selectedBackend);
                ShowAgentInlineStatus(status);
                ShowProviderSetupCard(status);
                AgentStopButton.Visibility = Visibility.Collapsed;
                ApprovalPanel.Hide();
                return;
            }

            ShowAgentInlineStatus(status);
            AgentStopButton.Visibility = Visibility.Collapsed;
            ApprovalPanel.Hide();
        }

        /// <summary>
        /// Puts the provider-setup message in the transcript with a button that
        /// opens the screen where a key is added.
        /// </summary>
        private void ShowProviderSetupCard(string content)
        {
            _terminalFailureCardContent = content;
            Messages.Add(new ChatMessageItem
            {
                Role = "assistant",
                Content = content,
                IsError = true,
                ActionLabel = AgentFailurePresentation.OpenProviderSettingsLabel,
                ActionKind = AgentFailurePresentation.OpenProviderSettingsAction,
            });
            ScrollChatToBottom();
        }

        /// <summary>
        /// Raised when an error message offers to open provider settings. The window
        /// owns the settings surface, so it performs the navigation.
        /// </summary>
        public event EventHandler? ProviderSettingsRequested;

        /// <summary>
        /// Applies the conversation typography to a message row once its template has
        /// been realised.
        /// </summary>
        /// <remarks>
        /// Set from code rather than bound in markup. A binding inside a data template
        /// cannot reach the typography by name: x:Bind is compiled before the resource
        /// exists, and a classic Binding with a StaticResource source asks the template
        /// to resolve an application resource while it is being parsed. The singleton is
        /// directly reachable here, which removes that lookup entirely.
        ///
        /// Both MarkdownBlock and TextBlock raise a change callback when FontSize is
        /// set, so a message already on screen re-renders when the size changes.
        /// </remarks>
        private void ApplyTranscriptTypography(DependencyObject root)
        {
            var typography = TranscriptTypography.Instance;

            foreach (var element in EnumerateDescendants(root))
            {
                switch (element)
                {
                    case MarkdownBlock reply:
                        reply.FontSize = typography.MessageFontSize;
                        break;
                    case TextBlock { Name: "AgentActivityText" } activity:
                        activity.FontSize = typography.ActivityFontSize;
                        break;
                }
            }
        }

        private static IEnumerable<DependencyObject> EnumerateDescendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(root, index);
                yield return child;
                foreach (var descendant in EnumerateDescendants(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// Re-applies typography to every realised message row, for a size change made
        /// while the transcript is on screen.
        /// </summary>
        private void RefreshTranscriptTypography()
        {
            if (ChatListView is null)
            {
                return;
            }

            foreach (object entry in ChatListView.Items)
            {
                if (ChatListView.ContainerFromItem(entry) is DependencyObject container)
                {
                    ApplyTranscriptTypography(container);
                }
            }
        }

        private void OnTranscriptTypographyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(TranscriptTypography.MessageFontSize)
                or nameof(TranscriptTypography.ActivityFontSize))
            {
                RefreshTranscriptTypography();
            }
        }

        private void OnMessageActionClick(object sender, RoutedEventArgs e)        {
            if (sender is not FrameworkElement element
                || element.Tag is not string action
                || !string.Equals(
                    action,
                    AgentFailurePresentation.OpenProviderSettingsAction,
                    StringComparison.Ordinal))
            {
                return;
            }

            ProviderSettingsRequested?.Invoke(this, EventArgs.Empty);
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

            var projectedHistory = SessionHistoryProjection.Project(history);
            var normalizedHistory = projectedHistory
                .Select(row => (row.Role, row.Content))
                .ToList();
            string? latestAssistant = projectedHistory
                .LastOrDefault(row => string.Equals(row.Role, "assistant", StringComparison.Ordinal))
                ?.Content ?? string.Empty;
            int lastAssistantIndex = SessionHistoryProjection.LastAssistantIndex(history);
            bool preserveStreamedReply = ChatHistoryReloadPolicy.ShouldPreserveStreamedReply(
                normalizedHistory,
                streamedReply);

            void Apply()
            {
                _suppressContextUsageUpdate = true;
                try
                {
                    if (preserveStreamedReply
                        && _activeAgentAssistantMessage is { } streamedMessage)
                    {
                        if (!Messages.Contains(streamedMessage))
                        {
                            Messages.Add(streamedMessage);
                        }

                        streamedMessage.Content = streamedReply;
                        streamedMessage.IsStreaming = false;
                        streamedMessage.Usage = _activeAgentUsage;
                        UpdateLiveTailText(streamedMessage);
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
                    var existingOffsets = Messages
                        .Where(m => m.Role == "assistant")
                        .Select(m => m.ActivitySplitOffsets
                            ?? (ReferenceEquals(m.AgentActivities, AgentActivities) && AgentActivities.Count > 0
                                ? TakeSplitOffsetSnapshot(AgentActivities)
                                : null))
                        .ToList();

                    DetachAgentActivitiesFromMessages();
                    Messages.Clear();
                    int assistantCount = 0;
                    // Live rows belong to the turn that just ended. They may only
                    // ride on history's last assistant when it is the final
                    // persisted message; otherwise the turn has no persisted
                    // assistant yet (deny/fail) and the rows need their own bubble.
                    bool attachLive = SessionHistoryProjection.AttachLiveToLastAssistant(
                        projectedHistory.Count, lastAssistantIndex, AgentActivities.Count);
                    bool liveAttached = false;
                    for (int index = 0; index < projectedHistory.Count; index++)
                    {
                        var row = projectedHistory[index];
                        // A machine tool envelope must never render as chat text
                        // (e.g. a repeated call that failed the run). Drop it.
                        if (!row.Render)
                        {
                            continue;
                        }

                        string role = row.Role;
                        string content = row.Content;

                        ObservableCollection<AgentActivityItem>? activities = null;
                        List<ActivitySplitOffset>? splitOffsets = null;
                        if (role == "assistant")
                        {
                            if (index == lastAssistantIndex)
                            {
                                if (attachLive)
                                {
                                    activities = new ObservableCollection<AgentActivityItem>(AgentActivities);
                                    splitOffsets = TakeSplitOffsetSnapshot(AgentActivities);
                                    liveAttached = true;
                                }
                                else
                                {
                                    activities = assistantCount < existingActivities.Count ? existingActivities[assistantCount] : null;
                                    splitOffsets = assistantCount < existingOffsets.Count ? existingOffsets[assistantCount] : null;
                                }
                            }
                            else if (assistantCount < existingActivities.Count)
                            {
                                activities = existingActivities[assistantCount];
                                splitOffsets = assistantCount < existingOffsets.Count ? existingOffsets[assistantCount] : null;
                            }
                            assistantCount++;
                        }

                        var restoredMessage = new ChatMessageItem
                        {
                            Role = role,
                            Content = role == "assistant" ? StripDisplayMarkers(content) : content,
                            Usage = index == lastAssistantIndex ? _activeAgentUsage : null,
                            AgentActivities = activities,
                            ActivitySplitOffsets = splitOffsets,
                        };
                        RebuildMessageSegments(restoredMessage);
                        Messages.Add(restoredMessage);
                    }

                    if (Services.ReloadActivitiesPolicy.AppendOrphanedActivities(AgentActivities.Count, liveAttached))
                    {
                        // The ended turn has live rows but no persisted assistant
                        // (denied/failed before any reply text). Keep the rows in
                        // their own trailing bubble instead of dropping them.
                        var orphan = new ChatMessageItem
                        {
                            Role = "assistant",
                            Content = StripDisplayMarkers(streamedReply ?? string.Empty),
                            Usage = _activeAgentUsage,
                            AgentActivities = new ObservableCollection<AgentActivityItem>(AgentActivities),
                            ActivitySplitOffsets = TakeSplitOffsetSnapshot(AgentActivities),
                        };
                        RebuildMessageSegments(orphan);
                        Messages.Add(orphan);
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


        private ScrollViewer? _chatScrollViewer;

        /// <summary>
        /// The ListView's own scroll host. The transcript ListView virtualizes,
        /// so scrolling, offsets and follow state all come from this host.
        /// Resolved lazily because the template part only exists after render.
        /// </summary>
        private ScrollViewer? ChatScrollViewer
        {
            get
            {
                if (_chatScrollViewer is null && ChatListView is not null)
                {
                    _chatScrollViewer = FindDescendantScrollViewer(ChatListView);
                }

                return _chatScrollViewer;
            }
        }

        private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
        {
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (current is ScrollViewer viewer)
                {
                    return viewer;
                }

                for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    queue.Enqueue(VisualTreeHelper.GetChild(current, index));
                }
            }

            return null;
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


        private void ScrollChatToBottom()
        {
            var scroller = ChatScrollViewer;
            if (scroller is null)
            {
                QueueScrollRetry();
                return;
            }

            if (scroller.ScrollableHeight > 0)
            {
                double verticalOffset = scroller.VerticalOffset;
                double maxOffset = scroller.ScrollableHeight;
                bool nearBottom = maxOffset - verticalOffset <= Services.UiTiming.ChatFollowThresholdPx;
                if (nearBottom || _followOutput)
                {
                    // Streaming snapshots and activity batches can arrive dozens
                    // of times per second. A ChangeView forces a layout pass, so
                    // coalesce them: the eye cannot track faster than this, but
                    // the dispatcher queue can drown in it and stall the feed
                    // into visible bursts.
                    long now = Environment.TickCount64;
                    if (now - _lastScrollChangeTick >= ScrollChangeMinIntervalMs)
                    {
                        _lastScrollChangeTick = now;
                        scroller.ChangeView(null, maxOffset, null, disableAnimation: true);
                    }
                    else
                    {
                        QueueScrollRetry();
                    }
                }
                else
                {
                    ShowNewMessagesPill();
                }
                return;
            }
            QueueScrollRetry();
        }

        private void QueueScrollRetry()
        {
            if (_scrollRetryQueued)
            {
                return;
            }

            _scrollRetryQueued = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RetryScrollAfterLayout);
        }

        private void RetryScrollAfterLayout()
        {
            _scrollRetryQueued = false;
            var scroller = ChatScrollViewer;
            if (scroller is null)
            {
                return;
            }

            if (scroller.ScrollableHeight > 0)
            {
                double verticalOffset = scroller.VerticalOffset;
                double maxOffset = scroller.ScrollableHeight;
                bool nearBottom = maxOffset - verticalOffset <= Services.UiTiming.ChatFollowThresholdPx;
                if (nearBottom)
                {
                    _lastScrollChangeTick = Environment.TickCount64;
                    scroller.ChangeView(null, maxOffset, null, disableAnimation: true);
                }
                else
                {
                    ShowNewMessagesPill();
                }
            }
        }

        private long _lastScrollChangeTick;
        private bool _scrollRetryQueued;
        private const long ScrollChangeMinIntervalMs = 120;

        // The draft rule (what survives a session switch, what an empty box means)
        // lives in ComposerDraftStore; the view only moves text in and out of the box.
        private readonly ComposerDraftStore _composerDrafts = new();
        private bool _newMessagesPillWired;
        private bool _followOutput = true;

        private void RestoreComposerDraft(string sessionId)
        {
            string draft = _composerDrafts.Restore(sessionId);
            if (ChatInputTextBox is not null
                && !string.Equals(ChatInputTextBox.Text, draft, StringComparison.Ordinal))
            {
                ChatInputTextBox.Text = draft;
            }
        }

        private void ClearComposerDraft(string sessionId) => _composerDrafts.Clear(sessionId);

        private void EnsureNewMessagesPillWired()
        {
            if (_newMessagesPillWired)
            {
                return;
            }

            var scroller = ChatScrollViewer;
            if (scroller is null)
            {
                return;
            }

            _newMessagesPillWired = true;
            scroller.ViewChanged += (_, _) => OnChatScrollViewChanged();
        }

        private void OnChatScrollViewChanged()
        {
            var scroller = ChatScrollViewer;
            if (scroller is null || NewMessagesPill is null)
            {
                return;
            }

            double maxOffset = scroller.ScrollableHeight;
            _followOutput = maxOffset <= 0
                || maxOffset - scroller.VerticalOffset <= Services.UiTiming.ChatFollowThresholdPx;
            if (_followOutput)
            {
                NewMessagesPill.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowNewMessagesPill()
        {
            if (_followOutput || NewMessagesPill is null)
            {
                return;
            }

            NewMessagesPill.Visibility = Visibility.Visible;
        }

        private void OnNewMessagesPillClick(object sender, RoutedEventArgs e)
        {
            _followOutput = true;
            if (NewMessagesPill is not null)
            {
                NewMessagesPill.Visibility = Visibility.Collapsed;
            }

            ChatScrollViewer?.ChangeView(null, ChatScrollViewer?.ScrollableHeight, null, disableAnimation: true);
            ChatInputTextBox?.Focus(FocusState.Programmatic);
        }

        private void JumpToLatestForNewTurn()
        {
            _followOutput = true;
            if (NewMessagesPill is not null)
            {
                NewMessagesPill.Visibility = Visibility.Collapsed;
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
            AbandonActiveAgentReply();
            UpdateSendButtonMode(isAgentRunning: false);
        }

        private async void OnAgentStopClick(object sender, RoutedEventArgs e)
        {
            await StopActiveAgentAsync();
        }

        private async void OnApprovalDecisionRequested(object? sender, Controls.AgentApprovalDecidedEventArgs e)
        {
            await RunAgentActionAsync(
                () => e.Approved
                    ? _agentController?.ApproveAsync() ?? Task.CompletedTask
                    : _agentController?.DenyAsync() ?? Task.CompletedTask,
                e.Approved ? "Submitting approval…" : "Denying approval…");
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

        /// <summary>Overrides the workspace for the next runs; null restores the app root default.</summary>
        public void SetRequestedWorkspace(string? workspacePath)
        {
            _requestedWorkspace = string.IsNullOrWhiteSpace(workspacePath) ? null : workspacePath.Trim();
        }

        public async Task OpenSessionAsync(string sessionId, string title)       {
            try
            {
                if (_apiService == null) return;
                // A workspace is single-owner while a run is non-terminal. Wait for
                // the cancellation commit before switching sessions; fire-and-forget
                // cancellation races the next create request and surfaces as the
                // misleading "workspace busy" error.
                await StopActiveAgentAsync(force: true);
                _composerDrafts.Save(_activeSessionId, ChatInputTextBox?.Text);
                _activeSessionId = sessionId;
                _activeSessionTitle = string.IsNullOrWhiteSpace(title) ? "New Chat" : title;
                ResetAgentState();
                JumpToLatestForNewTurn();
                RestoreComposerDraft(sessionId);
                if (AgentPresetBox is not null && AgentPresetBox.SelectedIndex != 0)
                {
                    AgentPresetBox.SelectedIndex = 0;
                }
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

            // The transcript scroll host stays full-width so its scrollbar docks
            // at the window edge; each message root centers its own 820 column.
            LayoutRealizedMessageColumns();
        }

        private void OnMessageColumnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement column)
            {
                // The row's template is realised by now, so its text elements exist and
                // can take the conversation size.
                ApplyTranscriptTypography(column);
                LayoutMessageColumn(column);
            }
        }

        private void LayoutMessageColumn(FrameworkElement column)
        {
            if (ChatListView is null || ChatListView.ActualWidth <= 0)
            {
                return;
            }

            double viewport = Math.Max(
                0,
                ChatListView.ActualWidth - ChatListView.Padding.Left - ChatListView.Padding.Right);
            column.Width = Math.Min(MessageColumnMaxWidth, viewport);
        }

        private void LayoutRealizedMessageColumns()
        {
            if (ChatListView is null || Messages.Count == 0)
            {
                return;
            }

            for (var i = 0; i < Messages.Count; i++)
            {
                if (ChatListView.ContainerFromIndex(i) is ListViewItem container
                    && container.ContentTemplateRoot is FrameworkElement root
                    && root.FindName("MessageColumn") is FrameworkElement column)
                {
                    LayoutMessageColumn(column);
                }
            }
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
            _contextMeterTimer?.Stop();
            _contextMeterTimer = null;
            _contextMeterScheduled = false;
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
            if (_modelSaveFailureTimer != null)
            {
                _modelSaveFailureTimer.Stop();
                _modelSaveFailureTimer.Tick -= OnModelSaveFailureTimerTick;
                _modelSaveFailureTimer = null;
            }
            MeteorShowerField?.Dispose();
        }

        private void DetachWaveTimer()
        {
            if (_waveTimer != null)
            {
                _waveTimer.Tick -= OnWaveTimerTick;
                _waveTimer = null;
            }
        }
        /// <summary>
        /// Reads one picked file into an attachment. Images are bounded by the
        /// image cap and decoded for the preview thumbnail; text and code files
        /// are bounded by the text cap and read as UTF-8.
        /// </summary>
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

                int maximumBytes = isImage ? MaxImageAttachmentBytes : MaxTextAttachmentBytes;
                if (props.Size > (ulong)maximumBytes)
                {
                    ShowAgentInlineStatus(isImage
                        ? $"Image '{file.Name}' exceeds the {maximumBytes / (1024 * 1024)} MB attachment limit."
                        : $"File '{file.Name}' exceeds the {maximumBytes / (1024 * 1024)} MB attachment limit.");
                    return;
                }

                using var readStream = (await file.OpenReadAsync()).AsStreamForRead();
                byte[]? bytes = await ReadBoundedBytesAsync(readStream, maximumBytes);
                if (bytes is null)
                {
                    ShowAgentInlineStatus($"File '{file.Name}' could not be read within the attachment limit.");
                    return;
                }

                string mimeType = isImage
                    ? Services.ChatAttachmentPolicy.MimeForExtension(file.Name)
                    : "text/plain";

                if (isImage)
                {
                    // Images are normalized on the client: over-large dimensions are
                    // scaled down and the backend's decoded-size limit is enforced
                    // before the bytes ever leave the desktop.
                    using var original = new System.IO.MemoryStream(bytes);
                    using var originalStream = original.AsRandomAccessStream();
                    var resized = await Services.ImageAttachmentResizer.ResizeIfNeededAsync(originalStream, mimeType);
                    bytes = resized.Bytes;
                    mimeType = resized.MimeType;
                }

                var item = new ImageAttachmentItem
                {
                    FileName = file.Name,
                    FilePath = file.Path,
                    ByteSize = bytes.LongLength,
                    IsImage = isImage,
                    MimeType = mimeType,
                };

                if (isImage)
                {
                    item.Base64Data = Convert.ToBase64String(bytes);
                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    using var previewBytes = new System.IO.MemoryStream(bytes);
                    using var previewStream = previewBytes.AsRandomAccessStream();
                    await bitmap.SetSourceAsync(previewStream);
                    item.PreviewImage = bitmap;
                }
                else
                {
                    item.TextContent = System.Text.Encoding.UTF8.GetString(bytes);
                }

                PendingAttachments.Add(item);
                AttachmentPreviewContainer.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChatView] Error adding attachment: {ex.Message}");
            }
        }

        /// <summary>
        /// Ctrl+V: a clipboard bitmap becomes a screenshot attachment, clipboard
        /// files go through the same path as the file picker.
        /// </summary>
        private async Task TryPasteImageFromClipboardAsync()
        {
            try
            {
                var package = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (package.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Bitmap))
                {
                    var streamRef = await package.GetBitmapAsync();
                    using var stream = await streamRef.OpenReadAsync();
                    using var readStream = stream.AsStreamForRead();
                    byte[]? bytes = await ReadBoundedBytesAsync(readStream, MaxImageAttachmentBytes);
                    if (bytes is null)
                    {
                        ShowAgentInlineStatus("The pasted image exceeds the attachment limit.");
                        return;
                    }

                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                    using var previewBytes = new System.IO.MemoryStream(bytes);
                    using var previewStream = previewBytes.AsRandomAccessStream();
                    await bitmap.SetSourceAsync(previewStream);

                    PendingAttachments.Add(new ImageAttachmentItem
                    {
                        FileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png",
                        ByteSize = bytes.LongLength,
                        Base64Data = Convert.ToBase64String(bytes),
                        MimeType = "image/png",
                        IsImage = true,
                        PreviewImage = bitmap,
                    });

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

        /// <summary>
        /// Drag-and-drop: every dropped storage file goes through the same
        /// attachment path as the picker, so the size and type limits apply once.
        /// </summary>
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
    }
}
